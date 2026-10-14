"""Ad-hoc sign a device app for TrollStore/AppSync or user-controlled re-signing."""
import argparse
import hashlib
import json
import pathlib
import plistlib
import subprocess
import shutil
import zipfile

from ios_packaging import MODES, VERSION, data_path, ipa_path, variant_for_mode

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--mode', choices=MODES, default='core-small')
args = parser.parse_args()
variant = variant_for_mode(args.mode)
archive_path = data_path(variant)
data = json.loads(archive_path.with_suffix('.json').read_text())
if data['version'] != VERSION or data['bytes'] != archive_path.stat().st_size:
    raise SystemExit('Selected data ZIP has stale or incompatible metadata')

root = pathlib.Path(__file__).resolve().parent.parent
products = root / "Builds/iOS/DerivedData/Build/Products/Release-iphoneos"
apps = list(products.glob("*.app"))
if len(apps) != 1:
    raise SystemExit(f"Expected one built device app in {products}, found {len(apps)}")
app = apps[0]
with (app / "Info.plist").open("rb") as stream:
    info = plistlib.load(stream)
if info.get("MinimumOSVersion") != "15.0":
    raise SystemExit("Device app must declare iOS 15.0")
if info.get("UIFileSharingEnabled") is not True or info.get("LSSupportsOpeningDocumentsInPlace") is not True:
    raise SystemExit("Device app must expose Documents through Files and file sharing")
unity_archive = app / 'Data/data.unity3d'
if not unity_archive.is_file():
    raise SystemExit('Missing compressed Unity player data; rebuild with CompressWithLz4HC')
with unity_archive.open('rb') as stream:
    if stream.read(8) != b'UnityFS\0':
        raise SystemExit('Invalid compressed Unity player data header')
embedded = app / 'Data/Raw/IDAS3GameData'
if embedded.is_symlink():
    raise SystemExit(f'Refusing symlink: {embedded}')
if embedded.exists():
    shutil.rmtree(embedded)
if args.mode == 'full':
    with archive_path.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != data['sha256']:
            raise SystemExit('Full data ZIP checksum mismatch')
    with zipfile.ZipFile(archive_path) as archive:
        raw = archive.read('IDAS3GameData/game-data.json')
        manifest = json.loads(raw)
        if (hashlib.sha256(raw).hexdigest() != data['manifestSHA256'] or
                manifest['version'] != VERSION or manifest['schema'] != 'idas3-game-data'):
            raise SystemExit('Full data manifest mismatch')
        expected = {'IDAS3GameData/' + entry['path']: entry for entry in manifest['files']}
        if set(archive.namelist()) != set(expected) | {'IDAS3GameData/game-data.json'}:
            raise SystemExit('Full data file list mismatch')
        for name, entry in expected.items():
            path = pathlib.PurePosixPath(entry['path'])
            if path.is_absolute() or '..' in path.parts or '\\' in entry['path']:
                raise SystemExit('Invalid embedded data path')
            destination = embedded / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            with archive.open(name) as source, destination.open('wb') as target:
                while block := source.read(1024 * 1024):
                    target.write(block); digest.update(block)
            if destination.stat().st_size != entry['bytes'] or digest.hexdigest() != entry['sha256']:
                raise SystemExit(f'Embedded data checksum mismatch: {name}')
        (embedded / 'game-data.json').write_bytes(raw)
info['IDAS3EmbeddedGameData'] = args.mode == 'full'
info['IDAS3PackageMode'] = 'full' if args.mode == 'full' else 'core'
with (app / 'Info.plist').open('wb') as stream:
    plistlib.dump(info, stream, fmt=plistlib.FMT_BINARY)
# Sign nested code first, then seal the app. A filename-derived CodeDirectory
# identifier can cause jailbreak installers to resolve a different data container
# from the one Files registers for CFBundleIdentifier (ARMSX1 commit 14a516b).
signed = {}
frameworks = sorted((app / "Frameworks").glob("*.framework"))
for bundle in [*frameworks, app]:
    bundle_info = plistlib.loads((bundle / "Info.plist").read_bytes())
    identifier = bundle_info["CFBundleIdentifier"]
    subprocess.run(["codesign", "--force", "--sign", "-", "--identifier", identifier,
                    "--timestamp=none", str(bundle)], check=True)
    details = subprocess.run(["codesign", "--display", "--verbose=4", str(bundle)],
                             check=True, capture_output=True, text=True).stderr
    if f"Identifier={identifier}\n" not in details or "Signature=adhoc\n" not in details:
        raise SystemExit(f"Unexpected signing identity for {bundle}")
    signed[str(bundle.relative_to(app))] = identifier
subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
output = ipa_path(args.mode)
temporary = output.with_suffix(".ipa.tmp")
with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as ipa:
    for file in sorted(app.rglob("*")):
        if file.is_file():
            ipa.write(file, pathlib.PurePosixPath("Payload", app.name, file.relative_to(app)))
temporary.replace(output)
with output.open('rb') as stream:
    digest = hashlib.file_digest(stream, "sha256").hexdigest()
manifest = {"file": output.name, "sha256": digest, "bytes": output.stat().st_size,
            "displayName": info['CFBundleDisplayName'], "bundleName": info['CFBundleName'],
            "appDirectory": app.name, "executable": info['CFBundleExecutable'],
            "appIcon": {"source": 'Assets/Branding/iOS/AppIcon.png',
                        "sha256": hashlib.sha256((root / 'Assets/Branding/iOS/AppIcon.png').read_bytes()).hexdigest(),
                        "artwork": 'https://www.steamgriddb.com/grid/803589'},
            "minimumOS": info["MinimumOSVersion"], "bundleIdentifier": info["CFBundleIdentifier"],
            "signing": "adhoc/fakesigned; TrollStore or AppSync; re-sign for standard sideloading",
            "signingIdentifier": info["CFBundleIdentifier"], "signedBundles": signed,
            "fileSharing": True,
            "unityDataCompression": "LZ4HC",
            "installedAppBytes": sum(p.stat().st_size for p in app.rglob('*') if p.is_file()),
            "nativeStorage": data['nativeStorage'],
            "romValidation": "enabled" if info.get("IDAS3RomValidationEnabled", True) else "disabled", "steamMultiplayer": "disabled",
            "eosMultiplayer": "disabled" if not info.get("IDAS3EosEnabled", True) else "enabled",
            "upstreamRelease": info["IDAS3UpstreamRelease"],
            "lanMultiplayer": "enabled", "discord": "disabled",
            "updates": "disabled" if not info.get("IDAS3UpdatesEnabled", True) else "enabled",
            "buildNumber": info["CFBundleVersion"], "touchControls": "analog stick, full-strength digital L2 brake/R2 gas, L1 downshift/R1 upshift",
            "virtualPedals": "255 anywhere inside a held pedal; zero outside or released",
            "languages": info["CFBundleLocalizations"],
            "languageSelection": "upstream English/Japanese/Simplified Chinese custom menus; device default, saved override",
            "defaultTouchLayout": "NFS3 Android: lower-left stick, lower-right gas inside/brake outside; side columns",
            "menu": "hold VIEW for 0.65 seconds at title/attract; START race pause > Options; no independent MENU button",
            "osd": "Options > Touch > OSD Display; live persistent FPS/CPU/GPU/RAM switch",
            "gravitySteering": "Options > Gameplay; live switch with 0-25% deadzone and 25-200% sensitivity; defaults off/5%/100%",
            "touchLayout": "Options > Touch; move, resize, opacity, grid, save/cancel/defaults, JSON import preview and export",
            "controlLabels": "START caption at bottom center; other controls use camera, headlamp, plus/minus, check/back and simple four-line pedals",
            "startInput": "independent pad START: story skip, frontend start and race pause; checkmark remains A/Confirm",
            "acceleratorConfirm": "R2 sends full right trigger during driving, plus A/Confirm for menu and tuning owners",
            "packageMode": info['IDAS3PackageMode'],
            "content": "bundled game data in Data/Raw/IDAS3GameData" if args.mode == 'full' else "external data v3 in Documents/IDAS3GameData",
            "supportedDataVariants": ['full', 'small'],
            "supportedDataVersions": list(range(1, VERSION + 1)),
            "supportedMusic": ['original SPSD/ADX/MS ADPCM WAV', 'MP3'],
            "music": data['music'] if args.mode == 'full' else "original + MP3; selected by data package",
            "dataPackage": {key: data[key] for key in ('file', 'sha256', 'bytes', 'unpackedBytes', 'version', 'destination')}}
output.with_suffix(".json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"IPA: {output}\nSHA256: {digest}\nBytes: {output.stat().st_size}")
