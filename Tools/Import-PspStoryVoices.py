#!/usr/bin/env python3
"""Import original Street Stage speech and rules; never execute PSP code."""
import argparse
import concurrent.futures
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def xbb(data):
    assert data[:4] == b"XBB\x01"
    result = {}
    for i in range(struct.unpack_from("<I", data, 4)[0]):
        offset, size, name, _ = struct.unpack_from("<4I", data, 32 + i * 16)
        name = data[name:data.index(0, name)].decode("ascii")
        assert offset + size <= len(data) and name not in result
        result[name] = data[offset:offset + size]
    return result


def import_media(iso, output, work):
    old_files = set()
    if (output / "voices.json").exists():
        old = json.loads((output / "voices.json").read_text())
        old_files = {c["file"] for b in old["banks"] for c in b["clips"]}
    paths = ["PSP_GAME/USRDIR/data/sound/voice"]
    subprocess.run(["bsdtar", "-xf", str(iso), "-C", str(work), *paths], check=True)
    base = work / "PSP_GAME/USRDIR/data"
    output.mkdir(parents=True, exist_ok=True)
    banks, jobs = [], {}
    for bank in sorted((base / "sound/voice").glob("ac*.bin")):
        data = bank.read_bytes()
        entries = xbb(data)
        words = entries["RACEWORDS_" + bank.stem.upper() + ".BIN"]
        count = struct.unpack_from("<H", words)[0]
        assert len(words) == 2 + 60 * count
        rules = []
        for i in range(count):
            row = words[2 + 60 * i:2 + 60 * (i + 1)]
            name = row[28:].split(b"\0")[0].decode("ascii")
            assert name in entries
            rules.append(dict(event=row[1], clip=name[:-4], sourceFlags=list(row[:28])))
        clips = []
        for name, audio in entries.items():
            if not name.endswith(".at3"):
                continue
            sha = hashlib.sha256(audio).hexdigest()
            target = hashlib.sha256(audio + b"mp3-mono22050-64k-asetpts-v1").hexdigest()[:24] + ".mp3"
            jobs.setdefault(target, audio)
            clips.append(dict(id=name[:-4], file=target, sourceSha256=sha))
        banks.append(dict(id=int(bank.stem[2:]), rules=rules, clips=clips,
                          sourceSha256=hashlib.sha256(data).hexdigest()))

    def encode(job):
        name, audio = job
        target = output / name
        if target.exists() and target.stat().st_size > 0:
            return
        source = work / (name + ".at3")
        source.write_bytes(audio)
        result = subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-y", "-i", str(source),
                        "-af", "aresample=22050,asetpts=N/SR/TB",
                        "-ac", "1", "-ar", "22050", "-c:a", "libmp3lame", "-b:a", "64k",
                        str(target)], capture_output=True, check=True)
        if result.stderr:
            raise RuntimeError(result.stderr.decode())
        pcm = subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-i", str(target),
                              "-f", "s16le", "-"], check=True, capture_output=True).stdout
        assert len(pcm) > 100 and any(pcm), "Empty decoded voice: " + name
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(encode, jobs.items()))
    spec = importlib.util.spec_from_file_location("subtitles", ROOT / "Tools/Build-StoryVoiceSubtitles.py")
    compiler = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(compiler)
    compiler.build(output, dict(banks=banks))
    for name in old_files - set(jobs):
        assert Path(name).name == name
        (output / name).unlink(missing_ok=True)
    print(json.dumps(dict(banks=len(banks), clips=sum(len(b["clips"]) for b in banks),
                          uniqueAudio=len(jobs),
                          bytes=sum(p.stat().st_size for p in output.iterdir())), indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "Native/data/story_voices")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="idas3-psp-import-") as directory:
        work = Path(directory)
        iso = args.image.resolve()
        if iso.suffix.lower() == ".chd":
            iso = work / "source.iso"
            subprocess.run(["chdman", "extractraw", "-i", str(args.image), "-o", str(iso)], check=True)
        import_media(iso, args.output, work)


if __name__ == "__main__":
    main()
