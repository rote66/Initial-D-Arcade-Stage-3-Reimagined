"""Stage native banks with the upstream lossless packer, preserving source data."""
from functools import lru_cache
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / 'Builds/iOS/Native-LZ4'
SOURCE = ROOT / 'Native/data'


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def build_tool(name='pack_native_assets'):
    targets = {'pack_native_assets': 'tools/pack_native_assets.cpp',
               'native_asset_storage_tests': 'tests/native_asset_storage_tests.cpp'}
    source = ROOT / 'Native'
    inputs = [source / targets[name], source / 'src/native_asset_storage.cpp',
              source / 'vendor/lz4/lz4.c', source / 'vendor/lz4/lz4hc.c']
    headers = [source / 'tools/native_asset_packing.h', source / 'src/native_asset_storage.h',
               source / 'vendor/lz4/lz4.h', source / 'vendor/lz4/lz4hc.h']
    fingerprint = hashlib.sha256(''.join(sha256(p) for p in inputs + headers).encode()).hexdigest()
    binary = CACHE / 'tools' / name
    stamp = binary.with_suffix('.sha256')
    if binary.is_file() and stamp.is_file() and stamp.read_text().strip() == fingerprint:
        return binary
    binary.parent.mkdir(parents=True, exist_ok=True)
    temporary = binary.with_suffix('.building')
    subprocess.run(['c++', '-std=c++20', '-O2', '-I', str(source / 'src'),
                    '-I', str(source / 'tools'), '-I', str(source / 'vendor/lz4'),
                    '-x', 'c++', *map(str, inputs), '-o', str(temporary)], check=True)
    temporary.replace(binary)
    stamp.write_text(fingerprint + '\n')
    return binary


@lru_cache(maxsize=1)
def prepare_native_storage():
    assets = sorted(p for p in SOURCE.rglob('*') if p.is_file() and p.suffix in {'.idastex', '.idasmesh'})
    originals = [{'path': p.relative_to(SOURCE).as_posix(), 'sourceBytes': p.stat().st_size,
                  'sourceSha256': sha256(p)} for p in assets]
    packer = build_tool()
    fingerprint = hashlib.sha256(json.dumps({'sources': originals, 'packer': sha256(packer)},
                                            sort_keys=True).encode()).hexdigest()
    manifest = CACHE / 'native-storage.json'
    data = CACHE / 'data'
    if manifest.is_file():
        old = json.loads(manifest.read_text())
        if old.get('sourceFingerprint') == fingerprint and all(
                (data / record['path']).is_file() and
                (data / record['path']).stat().st_size == record['bytes'] and
                sha256(data / record['path']) == record['sha256'] for record in old['files']):
            return old
    temporary = CACHE / 'data.installing'
    if temporary.exists():
        shutil.rmtree(temporary)
    temporary.mkdir(parents=True)
    for record, source in zip(originals, assets):
        target = temporary / record['path']
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    # The upstream tool compares every decoded byte to the original, including
    # CRC/boundary/EOF checks through the production reader, before replacement.
    result = subprocess.run([str(packer), str(SOURCE), str(temporary)],
                            check=True, capture_output=True, text=True)
    stats = json.loads(result.stdout)
    records = []
    for original, source in zip(originals, assets):
        if source.stat().st_size != original['sourceBytes'] or sha256(source) != original['sourceSha256']:
            raise RuntimeError('Native source changed during packing: ' + original['path'])
        target = temporary / original['path']
        with target.open('rb') as stream:
            packed = stream.read(8) == b'IDASLZ41'
        records.append({**original, 'bytes': target.stat().st_size,
                        'sha256': sha256(target), 'packed': packed})
    if stats['filesVerified'] != len(records) or stats['storedBytes'] != sum(r['bytes'] for r in records):
        raise RuntimeError('Native packer inventory differs from staged files')
    if data.exists():
        shutil.rmtree(data)
    temporary.replace(data)
    info = {'format': 'IDASLZ41', 'sourceFingerprint': fingerprint, **stats, 'files': records}
    manifest.write_text(json.dumps(info, indent=2) + '\n')
    print(f"Lossless native storage: {stats['sourceBytes']} -> {stats['storedBytes']} bytes; "
          f"{stats['filesVerified']} banks verified", flush=True)
    return info


def native_storage_summary():
    return {k: v for k, v in prepare_native_storage().items() if k != 'files'}


def native_storage_records():
    return {'IDAS3/data/' + r['path']: r for r in prepare_native_storage()['files']}
