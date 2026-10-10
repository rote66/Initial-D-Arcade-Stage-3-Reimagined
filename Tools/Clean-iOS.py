#!/usr/bin/env python3
"""Remove rebuildable iOS caches; retain IPA/data, sources, saves and reports."""
import argparse
from pathlib import Path
import shutil

PROJECT = Path(__file__).resolve().parents[1]
ROOT = PROJECT / 'Builds/iOS'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('target', choices=['device', 'simulator', 'all'])
parser.add_argument('--keep-simulator-app', action='store_true')
args = parser.parse_args()
base = ROOT if args.target == 'device' else ROOT / 'Simulator'
paths = [base / 'Xcode']
paths += [base / 'Xcode_Data']
if args.keep_simulator_app:
    if args.target != 'simulator':
        parser.error('--keep-simulator-app requires simulator')
    derived = base / 'DerivedData'
    paths += [p for p in derived.glob('*') if p.name != 'Build']
    paths += [p for p in (derived / 'Build').glob('*') if p.name != 'Products']
    paths += [p for p in (derived / 'Build/Products/Release-iphonesimulator').glob('*') if p.name != 'InitialDiOS.app']
else:
    paths += [base / 'DerivedData']
if args.target == 'all':
    paths = [ROOT / p for p in ('Xcode', 'Xcode_Data', 'DerivedData', 'Music-MP3', 'Native-LZ4',
        'Simulator/Xcode', 'Simulator/Xcode_Data', 'Simulator/DerivedData', 'LanPeer')]
    paths += [PROJECT / p for p in ('Native/build-ios', 'Native/build-ios-simulator',
        'Native/build-ios-lan-peer', 'Library/Bee', 'Library/PlayerDataCache', 'Library/BuildHistory',
        'Assets/Plugins/macOS/libIdas3Unity.dylib', 'Assets/Plugins/macOS/libIdas3Unity.dylib.meta')]
for path in paths:
    if path.is_symlink() or any(p.is_symlink() for p in path.parents if p != PROJECT and PROJECT in p.parents):
        raise SystemExit(f'Refusing symlink: {path}')
    if path.is_dir():
        shutil.rmtree(path)
        print(f'Removed generated directory: {path}')
    elif path.is_file():
        path.unlink()
