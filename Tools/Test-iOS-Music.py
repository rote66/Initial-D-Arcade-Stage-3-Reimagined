#!/usr/bin/env python3
"""Validate the production MP3 decoder and complete native race mixer offline."""
import json
from pathlib import Path
import subprocess
import tempfile
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]


def main():
    music = json.loads((ROOT / 'Builds/iOS/Music-MP3/music.json').read_text())
    sources = {track['source'].removeprefix('IDAS3/data/original_audio/streams/') for track in music['tracks']}
    build = ROOT / 'Native/build-portable'
    output = ROOT / 'Builds/iOS/Tests' / ('MP3-' + datetime.now().strftime('%Y%m%d-%H%M%S'))
    output.mkdir(parents=True)
    subprocess.run(['cmake', '-S', str(ROOT / 'Native'), '-B', str(build),
                    '-DIDAS3_PORTABLE_PLUGIN=ON', '-DCMAKE_BUILD_TYPE=Release'], check=True)
    subprocess.run(['cmake', '--build', str(build), '--parallel', '8'], check=True)
    results = []
    # Build a read-only view of the final data layout without duplicating GBs.
    with tempfile.TemporaryDirectory(prefix='idas3-mp3-native-root-') as task_temp:
        native = Path(task_temp)
        data = native / 'data'; data.mkdir()
        for path in (ROOT / 'Native/data').iterdir():
            if path.name != 'original_audio':
                (data / path.name).symlink_to(path, target_is_directory=path.is_dir())
        audio = data / 'original_audio'; audio.mkdir()
        for path in (ROOT / 'Native/data/original_audio').iterdir():
            if path.name != 'streams':
                (audio / path.name).symlink_to(path, target_is_directory=path.is_dir())
        streams = audio / 'streams'; streams.mkdir()
        for source in [ROOT / 'Native/data/original_audio/streams', ROOT / 'Builds/iOS/Music-MP3/IDAS3/data/original_audio/streams']:
            for path in source.rglob('*'):
                relative = path.relative_to(source)
                if not path.is_file() or (source == ROOT / 'Native/data/original_audio/streams' and relative.as_posix() in sources):
                    continue
                destination = streams / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.symlink_to(path)
        for name, args in [
            ('compressed_music', [str(build / 'compressed_music_tests'), str(ROOT / 'Native'), str(native)]),
            ('race_music_mixer', [str(build / 'race_music_catalog_portable'), str(native)]),
            ('portable_scene_mp3', [str(build / 'portable_scene_tests'), str(native)]),
        ]:
            with (output / (name + '.log')).open('w') as log:
                result = subprocess.run(args, stdout=log, stderr=subprocess.STDOUT)
            results.append({'name': name, 'passed': result.returncode == 0, 'exitCode': result.returncode,
                            'log': name + '.log'})
            print(name + ': ' + ('PASS' if result.returncode == 0 else 'FAIL'), flush=True)
            if result.returncode:
                print((output / (name + '.log')).read_text(), flush=True)
                break
    report = {'passed': len(results) == 3 and all(r['passed'] for r in results), 'buildNumber': '9',
              'scope': 'Production native decoder and actual EngineAudio mixer using the MP3 replacement layout. All 117 tracks, loop boundaries, pause/resume/mute/menu continuity and portable gameplay. Private temporary saves only; no system audio device or app-save changes.',
              'musicFingerprint': music['sourceFingerprint'], 'results': results}
    (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    print('Report: ' + str(output), flush=True)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
