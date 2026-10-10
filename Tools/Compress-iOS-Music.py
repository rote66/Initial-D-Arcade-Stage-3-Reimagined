#!/usr/bin/env python3
"""Generate 192 kbps music without modifying the original runtime resources."""
import hashlib
import json
import math
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'Builds/iOS/Music-MP3'
MANIFEST = OUTPUT / 'music.json'
BITRATE = 192
RATE = 44100


def source_tracks():
    text = (ROOT / 'Native/src/music_catalog.h').read_text()
    entries = re.findall(r'\{"([^"\n]+)",\s*"[^"\n]*",\s*(\d+),\s*"([^"\n]+)"', text)
    if len(entries) != 117 or len({entry[0] for entry in entries}) != 117:
        raise RuntimeError('Expected the complete stable 117-track native catalog')
    return [(key, int(stage), 'IDAS3/data/original_audio/streams/' + path) for key, stage, path in entries]


def main():
    tracks = source_tracks()
    inputs = [ROOT / 'Native/src/music_catalog.h', ROOT / 'Native/src/original_audio.cpp',
              ROOT / 'Native/src/original_audio.h', ROOT / 'Native/tools/audio_transcode_source.cpp',
              ROOT / 'Native/third_party/dr_libs/dr_mp3.h', Path(__file__)]
    inputs += [ROOT / 'Native/data' / path.removeprefix('IDAS3/data/') for _, _, path in tracks]
    digest = hashlib.sha256()
    for path in inputs:
        digest.update(str(path.relative_to(ROOT)).encode())
        digest.update(hashlib.file_digest(path.open('rb'), 'sha256').digest())
    fingerprint = digest.hexdigest()
    if MANIFEST.exists():
        old = json.loads(MANIFEST.read_text())
        if old.get('sourceFingerprint') == fingerprint and all(
            (OUTPUT / track['mp3']).is_file() and (OUTPUT / track['metadata']).is_file() and
            (OUTPUT / track['mp3']).stat().st_size == track['bytes'] for track in old['tracks']):
            print('192 kbps MP3 music unchanged: 117 tracks', flush=True)
            return
    ffmpeg = shutil.which('ffmpeg')
    if not ffmpeg:
        raise RuntimeError('ffmpeg with libmp3lame is required to create the MP3 music package')
    OUTPUT.mkdir(parents=True, exist_ok=True)
    records = []
    with tempfile.TemporaryDirectory(prefix='idas3-music-encode-') as task_temp:
        temp = Path(task_temp)
        helper = temp / 'audio_transcode_source'
        subprocess.run(['c++', '-std=c++20', '-O2', '-ffp-contract=off', '-fno-fast-math',
                        '-I', str(ROOT / 'Native/src'), str(ROOT / 'Native/tools/audio_transcode_source.cpp'),
                        str(ROOT / 'Native/src/original_audio.cpp'), '-o', str(helper)], check=True)

        def inspect(path):
            return json.loads(subprocess.run([str(helper), 'inspect', str(path)], check=True,
                                             capture_output=True, text=True).stdout)

        for index, (key, stage, original) in enumerate(tracks):
            source = ROOT / 'Native/data' / original.removeprefix('IDAS3/data/')
            pcm = temp / 'source.wav'
            original_info = json.loads(subprocess.run([str(helper), 'decode', str(source), str(pcm)],
                                      check=True, capture_output=True, text=True).stdout)
            target = str(Path(original).with_suffix('.mp3'))
            output = OUTPUT / target
            output.parent.mkdir(parents=True, exist_ok=True)
            temporary = output.with_suffix('.encoding.mp3')
            expected_frames = original_info['frames'] * RATE / original_info['sampleRate']
            target_frames = math.floor(expected_frames + .5)
            subprocess.run([ffmpeg, '-nostdin', '-hide_banner', '-loglevel', 'error', '-y', '-threads', '2',
                            '-i', str(pcm), '-map', '0:a:0', '-c:a', 'libmp3lame', '-b:a', f'{BITRATE}k',
                            '-af', f'aresample={RATE},atrim=end_sample={target_frames},asetpts=N/SR/TB',
                            '-ar', str(RATE), '-map_metadata', '-1', '-write_xing', '1', str(temporary)], check=True)
            encoded = inspect(temporary)
            if (encoded['sampleRate'] != RATE or encoded['channels'] != original_info['channels'] or
                encoded['frames'] < target_frames-1 or encoded['frames'] > target_frames+529 or
                encoded['maxSample'] <= encoded['minSample']):
                raise RuntimeError('MP3 gapless length or audio validation failed: ' + key)
            # Xing/LAME may leave a final sub-delay tail. Preserve the source's
            # exact resampled extent in the sidecar; the production loader trims it.
            final_frames = min(target_frames, encoded['frames'])
            # Non-looping SPSD headers can contain an unused arbitrary loop
            # pointer. Only an active source loop carries playback meaning.
            start = (math.floor(original_info['loopStart'] * RATE / original_info['sampleRate'] + .5)
                     if original_info['looping'] else 0)
            end = (final_frames if original_info['loopEnd'] == original_info['frames'] else
                   math.floor(original_info['loopEnd'] * RATE / original_info['sampleRate'] + .5))
            if not (0 <= start < end <= final_frames):
                raise RuntimeError('Invalid preserved music loop: ' + key)
            metadata = target + '.idasaudio'
            raw = struct.pack('<8sIIIQQQI', b'IDASMP3\0', 1, RATE, encoded['channels'],
                              final_frames, start, end, int(original_info['looping']))
            temporary.replace(output)
            (OUTPUT / metadata).write_bytes(raw)
            actual = inspect(output)
            if actual['loopStart'] != start or actual['loopEnd'] != end or actual['looping'] != original_info['looping']:
                raise RuntimeError('Production decoder did not preserve music loop: ' + key)
            records.append({'key': key, 'stage': stage, 'source': original, 'mp3': target, 'metadata': metadata,
                            'bytes': output.stat().st_size, 'sha256': hashlib.file_digest(output.open('rb'), 'sha256').hexdigest(),
                            'originalBytes': source.stat().st_size, 'original': original_info, 'decoded': actual,
                            'decoderTailFramesTrimmed': encoded['frames']-final_frames})
            pcm.unlink()
            print(f'MP3 {index + 1}/117: {key}', flush=True)
    manifest = {'schema': 'idas3-mp3-music', 'version': 1, 'bitrateKbps': BITRATE, 'sampleRate': RATE,
                'sourceFingerprint': fingerprint, 'tracks': records,
                'originalBytes': sum(t['originalBytes'] for t in records),
                'mp3Bytes': sum(t['bytes'] for t in records), 'metadataBytes': 48 * len(records)}
    MANIFEST.write_text(json.dumps(manifest, indent=2) + '\n')
    print(f"MP3 music: {manifest['originalBytes']} -> {manifest['mp3Bytes']} bytes", flush=True)


if __name__ == '__main__':
    main()
