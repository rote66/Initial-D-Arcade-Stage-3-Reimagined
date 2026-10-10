#!/usr/bin/env python3
"""Package reusable game content separately from the iOS core."""
import argparse
import hashlib
import json
import zipfile

from ios_packaging import ROOT, PACKS, VERSION, content_files, data_path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--variant', choices=['full', 'small'], default='small',
                    help='full: original music; small: the same content with 117 tracks at 192 kbps MP3')
parser.add_argument('--force', action='store_true')
args = parser.parse_args()
OUTPUT = data_path(args.variant)
files, music_info = content_files(args.variant)
fingerprint = hashlib.sha256(json.dumps([(str(p.relative_to(ROOT)), p.stat().st_size, p.stat().st_mtime_ns) for p, _ in files]).encode()).hexdigest()
sidecar = OUTPUT.with_suffix('.json')
if not args.force and OUTPUT.exists() and sidecar.exists():
    old = json.loads(sidecar.read_text())
    if old.get('sourceFingerprint') == fingerprint and old.get('version') == VERSION and old['bytes'] == OUTPUT.stat().st_size:
        print(f'Game data unchanged: {OUTPUT.name} ({old["bytes"]} bytes)', flush=True)
        raise SystemExit(0)
manifest = {'schema': 'idas3-game-data', 'version': VERSION, 'unpackedBytes': 0, 'files': []}
for file, path in files:
    with file.open('rb') as stream:
        sha = hashlib.file_digest(stream, 'sha256').hexdigest()
    size = file.stat().st_size
    manifest['unpackedBytes'] += size
    manifest['files'].append({'path': path, 'bytes': size, 'sha256': sha})
raw = (json.dumps(manifest, indent=2) + '\n').encode()
if not args.force and OUTPUT.exists() and sidecar.exists():
    old = json.loads(sidecar.read_text())
    if (old.get('manifestSHA256') == hashlib.sha256(raw).hexdigest() and
            old.get('version') == VERSION and old.get('bytes') == OUTPUT.stat().st_size):
        with OUTPUT.open('rb') as stream:
            intact = hashlib.file_digest(stream, 'sha256').hexdigest() == old.get('sha256')
        if intact:
            old['sourceFingerprint'] = fingerprint
            sidecar.write_text(json.dumps(old, indent=2) + '\n')
            print(f'Game data content unchanged: retaining {OUTPUT.name} ({old["bytes"]} bytes)', flush=True)
            raise SystemExit(0)
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
temporary = OUTPUT.with_suffix('.zip.tmp')
with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
    archive.writestr('IDAS3GameData/game-data.json', raw)
    for index, (file, name) in enumerate(files):
        archive.write(file, 'IDAS3GameData/' + name)
        if index % 4000 == 0:
            print(f'Data ZIP: {index}/{len(files)} files', flush=True)
temporary.replace(OUTPUT)
with OUTPUT.open('rb') as stream:
    sha = hashlib.file_digest(stream, 'sha256').hexdigest()
metadata = {'file': OUTPUT.name, 'sha256': sha, 'bytes': OUTPUT.stat().st_size,
            'schema': manifest['schema'], 'version': manifest['version'], 'files': len(files),
            'variant': args.variant, 'music': music_info,
            'unpackedBytes': manifest['unpackedBytes'], 'manifestSHA256': hashlib.sha256(raw).hexdigest(),
            'destination': 'Documents/IDAS3GameData', 'courses': list(PACKS), 'soundRoom': True,
            'sourceFingerprint': fingerprint}
sidecar.write_text(json.dumps(metadata, indent=2) + '\n')
print(f'Data: {OUTPUT}\nCompressed: {metadata["bytes"]} bytes; unpacked: {metadata["unpackedBytes"]} bytes; files: {len(files)}\nSHA256: {sha}', flush=True)
