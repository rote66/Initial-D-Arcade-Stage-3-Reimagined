"""Shared paths and content selection for the iOS build/package commands."""
import json
from pathlib import Path

import importlib.util

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'Builds/iOS'
PACKS = ('HAKONE', 'SADAMINE', 'ENNA', 'MYOGI_SPECIAL', 'USUI_SPECIAL', 'MOMIJI', 'TSUBAKI', 'GUNSAI', 'ODAWARA')
VERSION = 3
MODES = ('full', 'core-full', 'core-small')
MUSIC_ROOT = OUTPUT / 'Music-MP3'


def variant_for_mode(mode):
    return 'small' if mode == 'core-small' else 'full'


def data_path(variant):
    return OUTPUT / f'InitialD-GameData-v{VERSION}-{"mp3" if variant == "small" else "full"}.zip'


def ipa_path(mode):
    # Both external data variants use the SAME core binary and output filename.
    return OUTPUT / f'InitialD-iOS15-{"full" if mode == "full" else "core"}.ipa'


def original_tracks():
    spec = importlib.util.spec_from_file_location('idas3_music_compressor', ROOT / 'Tools/Compress-iOS-Music.py')
    compressor = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(compressor)
    return compressor.source_tracks()


def content_files(variant):
    sources = [(ROOT / 'Native/data', 'IDAS3/data'), (ROOT / 'Assets/StreamingAssets/SoundRoom', 'SoundRoom')]
    sources += [(ROOT / 'RuntimeAssets' / pack, pack) for pack in PACKS]
    tracks = original_tracks()
    music = None
    if variant == 'small':
        if not (MUSIC_ROOT / 'music.json').is_file():
            raise SystemExit('Run Tools/Compress-iOS-Music.py to prepare all 117 MP3 tracks')
        music = json.loads((MUSIC_ROOT / 'music.json').read_text())
        if (music.get('bitrateKbps') != 192 or len(music.get('tracks', [])) != 117 or
                {t['source'] for t in music['tracks']} != {path for _, _, path in tracks}):
            raise SystemExit('MP3 cache does not match the complete native music catalog')
    replaced = {t['source'] for t in music['tracks']} if music else set()
    files = []
    found = set()
    for directory, destination in sources:
        if not directory.is_dir():
            raise SystemExit(f'Missing content: {directory}')
        for file in sorted(directory.rglob('*')):
            if file.is_file() and file.name != '.DS_Store' and file.suffix != '.meta':
                path = destination + '/' + file.relative_to(directory).as_posix()
                found.add(path)
                if path not in replaced:
                    files.append((file, path))
    if not {path for _, _, path in tracks}.issubset(found):
        raise SystemExit('Original runtime content is missing a full music track')
    if music:
        for track in music['tracks']:
            for key in ('mp3', 'metadata'):
                file = MUSIC_ROOT / track[key]
                if not file.is_file():
                    raise SystemExit(f'Missing MP3 cache file: {file}')
                files.append((file, track[key]))
    names = [name.casefold() for _, name in files]
    if len(set(names)) != len(names):
        raise SystemExit('Case-insensitive data paths must be unique')
    music_info = ({'tracks': 117, 'format': 'MP3', 'bitrateKbps': 192, 'sampleRate': 44100,
                   'gapless': True, 'loopMetadata': 'per-track .mp3.idasaudio', 'originalSourcesRetained': True}
                  if music else {'tracks': 117, 'format': 'original', 'originalSourcesRetained': True})
    return files, music_info
