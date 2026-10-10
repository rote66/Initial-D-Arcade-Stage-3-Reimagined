#!/usr/bin/env python3
"""Validate a full IPA, or the universal core with either external data variant."""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import subprocess
import tempfile
import zipfile

from ios_packaging import (ROOT, MODES, VERSION, MUSIC_ROOT, PACKS, content_files, data_path,
                           ipa_path, original_tracks, variant_for_mode)
from ios_native_storage import native_storage_records, native_storage_summary

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--mode', choices=MODES, default='core-small')
args = parser.parse_args()
variant = variant_for_mode(args.mode)
ipa_path = ipa_path(args.mode)
manifest = json.loads(ipa_path.with_suffix('.json').read_text())
data_path = data_path(variant)
data_info = json.loads(data_path.with_suffix('.json').read_text())
source_files, _ = content_files(variant)
sources = {name: path for path, name in source_files}
native_records = native_storage_records()
checks = []
def check(value, description):
    if not value:
        raise SystemExit('IPA validation failed: ' + description)
    checks.append(description)
with ipa_path.open('rb') as stream:
    check(hashlib.file_digest(stream, 'sha256').hexdigest() == manifest['sha256'], 'SHA256 matches manifest')
with zipfile.ZipFile(ipa_path) as ipa:
    installed_app_bytes = sum(p.file_size for p in ipa.infolist())
    check(ipa.testzip() is None, 'ZIP resource CRCs')
    prefix = 'Payload/InitialDiOS.app/'
    if manifest.get('unityDataCompression'):
        check(manifest['unityDataCompression'] == 'LZ4HC', 'Lossless Unity resource compression')
        with ipa.open(prefix + 'Data/data.unity3d') as stream:
            check(stream.read(8) == b'UnityFS\0', 'Compressed UnityFS player data')
        for loose in ['resources.assets', 'resources.assets.resS']:
            name = prefix + 'Data/' + loose
            check(name not in ipa.namelist() or ipa.getinfo(name).file_size == 0,
                  'No duplicate loose Unity resources: ' + loose)
        check(installed_app_bytes == manifest['installedAppBytes'], 'Installed app size')
    info = plistlib.loads(ipa.read(prefix + 'Info.plist'))
    check(info['MinimumOSVersion'] == '15.0', 'Info.plist minimum iOS 15.0')
    check(info['CFBundleVersion'] == manifest['buildNumber'], 'Build number matches manifest')
    check(info.get('IDAS3RomValidationEnabled') == (manifest['romValidation'] == 'enabled'), 'ROM macro matches manifest')
    check(info.get('IDAS3UpdatesEnabled') is False, 'Updates disabled')
    check(info.get('CFBundleLocalizations') == manifest['languages'] == ['en', 'ja', 'zh-Hans'], 'Supported app languages')
    check(info.get('CFBundleDevelopmentRegion') == 'en', 'English language fallback')
    check(bool(info.get('NSMotionUsageDescription')), 'Motion privacy description')
    check(bool(info.get('NSLocalNetworkUsageDescription')), 'Local network privacy description')
    check(info.get('IDAS3LanMultiplayerEnabled') is True, 'LAN multiplayer enabled')
    check(info.get('IDAS3SteamEnabled') is False and info.get('IDAS3DiscordEnabled') is False, 'Steam and Discord disabled')
    check(info.get('UIFileSharingEnabled') is True, 'Documents file sharing enabled')
    check(info.get('LSSupportsOpeningDocumentsInPlace') is True, 'Documents available in Files')
    check(info['CFBundleIdentifier'] == manifest['signingIdentifier'], 'Stable bundle/signing identifier')
    check(not any('steam_api' in name.lower() for name in ipa.namelist()), 'No Steam API binary')
    data_version = info.get('IDAS3GameDataVersion')
    check(data_version == manifest['dataPackage']['version'] == data_info['version'] == VERSION, 'Shared game data version')
    check(manifest['supportedDataVariants'] == ['full', 'small'], 'Universal full/small data compatibility')
    check(manifest['supportedDataVersions'] == list(range(1, VERSION + 1)), 'Legacy original data compatibility')
    check(info.get('IDAS3EmbeddedGameData') == (args.mode == 'full'), 'Embedded/external data selection')
    check(info.get('IDAS3PackageMode') == manifest['packageMode'] == ('full' if args.mode == 'full' else 'core'), 'Package mode')
    embedded_prefix = prefix + 'Data/Raw/IDAS3GameData/'
    check(any(name.startswith(embedded_prefix) for name in ipa.namelist()) == (args.mode == 'full'), 'Full embeds data; core excludes data')
    for folder in ['IDAS3', 'SoundRoom', 'SteamInput', *PACKS]:
        check(not any(name.startswith(prefix + 'Data/Raw/' + folder + '/') for name in ipa.namelist()), 'Core excludes ' + folder)
    binaries = {}
    signatures = {}
    with tempfile.TemporaryDirectory(prefix='idas3-ipa-validation-') as scratch:
        ipa.extractall(scratch)
        app = Path(scratch) / prefix
        for name in ipa.namelist():
            mode = ipa.getinfo(name).external_attr >> 16
            if mode and not name.endswith('/'):
                (Path(scratch) / name).chmod(mode & 0o777)
        subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True, capture_output=True)
        check(True, 'Whole app and nested resource signatures verify')
        for name in ['InitialDiOS', 'Frameworks/UnityFramework.framework/UnityFramework', 'Frameworks/UnityRuntime.framework/UnityRuntime']:
            path = app / name
            architecture = subprocess.check_output(['lipo', '-archs', str(path)], text=True).strip()
            commands = subprocess.check_output(['otool', '-l', str(path)], text=True)
            check(architecture == 'arm64', name + ' ARM64 device architecture')
            check('platform 2\n' in commands and 'minos 15.0\n' in commands, name + ' iOS 15 Mach-O deployment target')
            binaries[name] = {'architecture': architecture, 'platform': 'IOS', 'minimumOS': '15.0'}
            bundle = app if name == 'InitialDiOS' else path.parent
            bundle_info = plistlib.loads((bundle / 'Info.plist').read_bytes())
            details = subprocess.run(['codesign', '--display', '--verbose=4', str(bundle)], check=True, capture_output=True, text=True).stderr
            identifier = bundle_info['CFBundleIdentifier']
            check('Signature=adhoc\n' in details, name + ' ad-hoc signature')
            check(f'Identifier={identifier}\n' in details, name + ' signing identity matches bundle')
            check(manifest['signedBundles'][str(bundle.relative_to(app))] == identifier, name + ' signing manifest matches')
            entitlements = subprocess.run(['codesign', '--display', '--entitlements', ':-', str(bundle)], check=True, capture_output=True).stdout
            values = plistlib.loads(entitlements) if entitlements else {}
            check(not any(key in values for key in ['dynamic-codesigning', 'com.apple.security.cs.allow-jit', 'com.apple.security.cs.allow-unsigned-executable-memory']), name + ' no unnecessary JIT entitlements')
            signatures[name] = {'type': 'adhoc', 'identifier': identifier, 'entitlements': values}
        framework = app / 'Frameworks/UnityFramework.framework/UnityFramework'
        links = subprocess.check_output(['otool', '-L', str(framework)], text=True)
        check('CoreMotion.framework' in links, 'CoreMotion linked')
        check('UniformTypeIdentifiers.framework' in links, 'ZIP document picker linked')
check(data_path.stat().st_size == data_info['bytes'], 'Data ZIP size')
with data_path.open('rb') as stream:
    check(hashlib.file_digest(stream, 'sha256').hexdigest() == data_info['sha256'], 'Data ZIP SHA256')
# A core manifest records the data emitted in that invocation. The same core
# must also validate with the other ZIP without requiring a different build.
if manifest['dataPackage']['file'] == data_path.name:
    check(manifest['dataPackage']['sha256'] == data_info['sha256'], 'Emitted data checksum matches IPA manifest')
packs = {}; native_count = 0; sound_count = 0
content_prefix = embedded_prefix if args.mode == 'full' else 'IDAS3GameData/'
with zipfile.ZipFile(ipa_path if args.mode == 'full' else data_path) as archive:
    raw = archive.read(content_prefix + 'game-data.json')
    check(hashlib.sha256(raw).hexdigest() == data_info['manifestSHA256'], 'Data manifest SHA256')
    data = json.loads(raw)
    check(data.get('nativeStorage') == data_info.get('nativeStorage') == native_storage_summary(),
          'Upstream lossless native packing enabled and inventory matches')
    check(data['nativeStorage']['packedFiles'] > 0 and data['nativeStorage']['savedBytes'] > 0,
          'Native banks retain compression after ZIP extraction')
    check(data['version'] == data_version and data['schema'] == 'idas3-game-data', 'Data schema/version')
    names = [name for name in archive.namelist() if name.startswith(content_prefix)]
    name_set = set(names)
    music = None
    if variant == 'small':
        music = json.loads((MUSIC_ROOT / 'music.json').read_text())
        check(len(music['tracks']) == 117 and music['bitrateKbps'] == 192, 'Complete 192 kbps MP3 music catalog')
        for track in music['tracks']:
            check(content_prefix + track['source'] not in name_set, 'Original full song excluded: ' + track['key'])
            check(content_prefix + track['mp3'] in name_set and content_prefix + track['metadata'] in name_set,
                  'MP3 and preserved loop metadata included: ' + track['key'])
    else:
        for key, _, path in original_tracks():
            check(content_prefix + path in name_set, 'Original full song included: ' + key)
            replacement = str(Path(path).with_suffix('.mp3'))
            check(content_prefix + replacement not in name_set and content_prefix + replacement + '.idasaudio' not in name_set,
                  'No duplicate converted full song: ' + key)
    expected = {content_prefix + name for name in sources} | {content_prefix + 'game-data.json'}
    check(name_set == expected and len(names) == len(set(name.casefold() for name in names)) == len(data['files']) + 1, 'Exact data file list')
    check(data['unpackedBytes'] == sum(file['bytes'] for file in data['files']), 'Data unpacked size')
    for file in data['files']:
        name = content_prefix + file['path']
        check(archive.getinfo(name).file_size == file['bytes'], 'data size: ' + file['path'])
        with archive.open(name) as stream:
            check(hashlib.file_digest(stream, 'sha256').hexdigest() == file['sha256'], 'data SHA256/CRC: ' + file['path'])
        if file['path'].startswith('IDAS3/data/'):
            native_count += 1
        elif file['path'].startswith('SoundRoom/'):
            sound_count += 1
        else:
            pack = file['path'].split('/')[0]; packs[pack] = packs.get(pack, 0) + 1
        source = sources[file['path']]
        check(source.is_file() and source.stat().st_size == file['bytes'], 'source data: ' + file['path'])
        if file['path'] in native_records:
            bank = native_records[file['path']]
            check(file.get('sourceSha256') == bank['sourceSha256'] and file.get('sourceBytes') == bank['sourceBytes'],
                  'Original native bank provenance: ' + file['path'])
            with archive.open(name) as stream:
                check((stream.read(8) == b'IDASLZ41') == bank['packed'], 'Native storage header: ' + file['path'])
report = {'passed': True, 'sha256': manifest['sha256'], 'checks': len(checks), 'nativeFiles': native_count,
          'signatures': signatures, 'fileSharing': True, 'languages': manifest['languages'],
          'packs': packs, 'soundRoomFiles': sound_count, 'binaries': binaries, 'zipCRC': 'passed', 'romValidation': manifest['romValidation'], 'updates': 'disabled',
          'externalData': {'file': data_path.name, 'sha256': data_info['sha256'], 'version': data_version, 'files': len(data['files']), 'unpackedBytes': data['unpackedBytes'], 'contentSHA256': 'all passed'}}
report['packageMode'] = manifest['packageMode']
report['testedDataVariant'] = variant
report['buildNumber'] = info['CFBundleVersion']
report['supportedDataVariants'] = ['full', 'small']
report['nativeStorage'] = native_storage_summary()
report['unityDataCompression'] = manifest.get('unityDataCompression', 'legacy loose')
report['installedAppBytes'] = installed_app_bytes
if music:
    report['mp3Music'] = {'tracks': 117, 'bitrateKbps': 192, 'bytes': music['mp3Bytes'], 'originalFullSongsExcluded': True, 'loopSidecarsIncluded': True}
ipa_path.with_suffix('.validation.json').write_text(json.dumps(report, indent=2) + '\n')
# Keep independent evidence that one core validates with both ZIPs.
ipa_path.with_suffix(f'.{variant}.validation.json').write_text(json.dumps(report, indent=2) + '\n')
print(f'IPA validation PASS ({len(checks)} checks)')
