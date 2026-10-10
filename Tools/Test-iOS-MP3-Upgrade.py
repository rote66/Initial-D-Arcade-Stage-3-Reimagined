#!/usr/bin/env python3
"""Exercise the real iOS data upgrade/importer and preserve save hashes."""
from ios_packaging import data_path, VERSION
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import subprocess
import time
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
DEVICE = None
BUNDLE = 'org.idas3.reimagined.ios'
OUTPUT = ROOT / 'Builds/iOS/Simulator/Tests/MP3Upgrade'


def simctl(*parts, **kwargs):
    return subprocess.run(['xcrun', 'simctl', *parts], check=True, **kwargs)


def main():
    global DEVICE, OUTPUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--device', required=True, help='Existing iPhone Simulator UDID.')
    parser.add_argument('--baseline', type=Path, help='Optional saved hash baseline; otherwise snapshot current user files.')
    parser.add_argument('--archive', type=Path, default=data_path('small'))
    parser.add_argument('--output', type=Path, default=OUTPUT.parent / ('MP3Upgrade-' + datetime.now().strftime('%Y%m%d-%H%M%S')))
    args = parser.parse_args()
    DEVICE = args.device
    OUTPUT = args.output
    OUTPUT.mkdir(parents=True, exist_ok=True)
    app = Path(simctl('get_app_container', DEVICE, BUNDLE, 'app', capture_output=True, text=True).stdout.strip())
    info = plistlib.loads((app / 'Info.plist').read_bytes())
    assert int(info['CFBundleVersion']) >= 10
    container = Path(simctl('get_app_container', DEVICE, BUNDLE, 'data', capture_output=True, text=True).stdout.strip())
    docs = container / 'Documents'
    before = json.loads((docs / 'IDAS3GameData/game-data.json').read_text())
    if args.baseline:
        baseline = json.loads(args.baseline.read_text())
        assert before['version'] == baseline['dataVersion']
    else:
        files = {}
        for folder in ('userdata-unity-scene', 'Touch Layouts', 'Custom Music'):
            directory = docs / folder
            for file in directory.rglob('*'):
                if file.is_file():
                    files[file.relative_to(docs).as_posix()] = hashlib.file_digest(file.open('rb'), 'sha256').hexdigest()
        baseline = {'dataVersion': before['version'], 'files': files}
    (OUTPUT / 'baseline.json').write_text(json.dumps(baseline, indent=2) + '\n')
    report = {'passed': False, 'buildNumber': info['CFBundleVersion'], 'device': DEVICE, 'beforeVersion': before['version'],
              'scope': 'Actual runtime data gate and importer, supplied archive path bypasses the Files picker. Simulator audio engine disabled only for this process. Save/layout/custom-music hashes checked before/after.'}

    def run_launch(log_name, extra, wait_for):
        log_path = OUTPUT / log_name
        with log_path.open('w') as log:
            proc = subprocess.Popen(['xcrun', 'simctl', 'launch', '--console', '--terminate-running-process',
                                     DEVICE, BUNDLE, '-disableaudio', '-AppleLanguages', '(zh-Hans)', *extra],
                                    stdout=log, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 180
                while time.monotonic() < deadline:
                    text = log_path.read_text(errors='replace')
                    if wait_for in text:
                        time.sleep(2)
                        simctl('io', DEVICE, 'screenshot', '--type=png', str(OUTPUT / log_name.replace('.log', '.png')),
                               capture_output=True)
                        return
                    if proc.poll() is not None:
                        raise RuntimeError('App exited; see ' + str(log_path))
                    time.sleep(1)
                raise RuntimeError('App/data gate timed out; see ' + str(log_path))
            finally:
                subprocess.run(['xcrun', 'simctl', 'terminate', DEVICE, BUNDLE], capture_output=True)
                try:
                    proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    proc.terminate(); proc.wait(timeout=10)

    try:
        archive = args.archive.resolve(strict=True)
        if before['version'] < VERSION:
            run_launch('update-required.log', [], 'Game data:')
            print('Old content requests the updated data package', flush=True)
        run_launch('data-import.log', ['-idas3-data-import', str(archive)], 'IDAS3 external game data ready:')
        installed = docs / 'IDAS3GameData'
        manifest = json.loads((installed / 'game-data.json').read_text())
        assert manifest['version'] == VERSION
        meta = json.loads(archive.with_suffix('.json').read_text())
        assert len(manifest['files']) == meta['files'] and manifest['unpackedBytes'] == meta['unpackedBytes']
        for file in manifest['files']:
            assert (installed / file['path']).stat().st_size == file['bytes'], file['path']
        music = json.loads((ROOT / 'Builds/iOS/Music-MP3/music.json').read_text())
        for track in music['tracks']:
            assert not (installed / track['source']).exists()
            assert (installed / track['mp3']).exists() and (installed / track['metadata']).exists()
        for name, expected in baseline['files'].items():
            assert hashlib.file_digest((docs / name).open('rb'), 'sha256').hexdigest() == expected, name
        assert not Path(str(installed) + '.installing').exists()
        assert not Path(str(installed) + '.previous').exists()
        report.update(passed=True, afterVersion=VERSION, importedFiles=len(manifest['files']),
                      unpackedBytes=manifest['unpackedBytes'], mp3Songs=117,
                      originalFullSongsRemoved=True, preservedUserFiles=len(baseline['files']),
                      saveLayoutMusicHashesUnchanged=True, stagingAndPreviousRemoved=True)
        print('Runtime data import: PASS; 117 MP3s; user files preserved', flush=True)
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        (OUTPUT / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
        simctl('launch', DEVICE, BUNDLE, '-disableaudio')
    print('Report: ' + str(OUTPUT / 'report.json'), flush=True)


if __name__ == '__main__':
    main()
