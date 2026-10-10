#!/usr/bin/env python3
"""Test real TCP races between the current iOS Simulator and a temporary macOS native peer."""
import argparse
from datetime import datetime
import json
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = 'org.idas3.reimagined.ios'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--device', default='booted')
    parser.add_argument('--peer-app', type=Path, default=ROOT / 'Builds/iOS/LanPeer/InitialDLanPeer.app')
    args = parser.parse_args()
    peer_app = args.peer_app.resolve(strict=True)
    info = __import__('plistlib').loads((peer_app / 'Contents/Info.plist').read_bytes())
    peer_exe = peer_app / 'Contents/MacOS' / info['CFBundleExecutable']
    stamp = datetime.now().strftime('%Y%m%d-%H%M%S')
    output = ROOT / 'Builds/iOS/Simulator/Tests' / (stamp + '-lan')
    output.mkdir(parents=True)
    container = Path(subprocess.check_output(['xcrun', 'simctl', 'get_app_container', args.device, BUNDLE, 'data'], text=True).strip())
    host_root = container / 'Documents' / ('simulator-test-' + stamp + '-lan')
    peer_root = output / 'peer'
    data = container / 'Documents/IDAS3GameData'
    if not (data / 'game-data.json').is_file():
        raise RuntimeError('Install the existing game data in Simulator first.')
    streaming = peer_app / 'Contents/Resources/Data/StreamingAssets'
    if streaming.is_symlink():
        streaming.unlink()
    elif streaming.exists():
        shutil.rmtree(streaming)
    streaming.symlink_to(data, target_is_directory=True)
    common = ['-idas3-ios-lan-check', '-idas3-multiplayer-port', '27830', '-disableaudio']
    processes = []
    logs = []
    try:
        host_log = (output / 'host.log').open('w'); logs.append(host_log)
        host = subprocess.Popen(['xcrun', 'simctl', 'launch', '--console', '--terminate-running-process',
            args.device, BUNDLE, '-idas3-multiplayer-smoke', str(host_root),
            '-idas3-multiplayer-role', 'host', '-idas3-multiplayer-peer-output', str(peer_root),
            '-AppleLanguages', '(en)', *common], stdout=host_log, stderr=subprocess.STDOUT)
        processes.append(host)
        deadline = time.monotonic() + 90
        while not (host_root / 'room.json').is_file():
            if time.monotonic() > deadline or host.poll() is not None:
                raise RuntimeError('Simulator did not open a LAN room; see host.log')
            time.sleep(.5)
        room = json.loads((host_root / 'room.json').read_text())['code']
        print('Simulator hosting:', room, flush=True)
        peer_log = (output / 'peer.log').open('w'); logs.append(peer_log)
        peer = subprocess.Popen([str(peer_exe), '-idas3-multiplayer-smoke', str(peer_root),
            '-idas3-multiplayer-role', 'join', '-idas3-multiplayer-address', room,
            '-idas3-multiplayer-peer-output', str(host_root), '-logFile', '-', *common],
            stdout=peer_log, stderr=subprocess.STDOUT)
        processes.append(peer)
        deadline = time.monotonic() + 240
        paths = [host_root / 'report.json', peer_root / 'report.json']
        previous = None
        while not all(p.is_file() for p in paths):
            phases = []
            for path in [host_root, peer_root]:
                status = path / 'status.json'
                phases.append(json.loads(status.read_text()).get('phase') if status.is_file() else 'starting')
            if phases != previous:
                print('Host / peer:', ' / '.join(phases), flush=True); previous = phases
            for path in paths:
                if path.is_file() and not json.loads(path.read_text())['passed']:
                    raise RuntimeError(json.loads(path.read_text()).get('error'))
            if host.poll() is not None and not paths[0].is_file():
                raise RuntimeError('Simulator exited without a report; see host.log')
            if peer.poll() is not None and not paths[1].is_file():
                raise RuntimeError('Native peer exited without a report; see peer.log')
            if time.monotonic() > deadline:
                raise RuntimeError('LAN integration diagnostic timed out')
            time.sleep(.5)
        shutil.copytree(host_root, output / 'host')
        reports = {name: json.loads(path.read_text()) for name, path in zip(['host', 'peer'], paths)}
        (output / 'results.json').write_text(json.dumps(reports, indent=2))
        for name, report in reports.items():
            print(f"{name}: {'PASS' if report['passed'] else 'FAIL'} ({report['checks']} checks, {report['snapshotsReceived']} snapshots)", flush=True)
        return 0 if all(r['passed'] for r in reports.values()) else 1
    finally:
        subprocess.run(['xcrun', 'simctl', 'terminate', args.device, BUNDLE], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for process in processes:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait()
        for log in logs:
            log.close()
        if host_root.exists():
            if not (output / 'host').exists():
                shutil.copytree(host_root, output / 'host')
            shutil.rmtree(host_root)
        print('LAN reports:', output, flush=True)


if __name__ == '__main__':
    raise SystemExit(main())
