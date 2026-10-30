#!/usr/bin/env python3
"""Audit original PSP voice banks and produce Japanese ASR review candidates."""
import argparse
import concurrent.futures
import gzip
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import wave

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("psp_media", ROOT / "Tools/Import-PspStoryVoices.py")
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def archive_read(iso, name):
    return subprocess.check_output(["bsdtar", "-xOf", str(iso), name])


def prepare(args):
    iso = args.iso.resolve()
    inventory = subprocess.check_output(["bsdtar", "-tf", str(iso)], text=True).splitlines()
    banks = [p for p in inventory if re.fullmatch(r"PSP_GAME/USRDIR/data/sound/voice/ac\d\d.bin", p)]
    if len(banks) != 30:
        raise ValueError(f"Expected 30 original voice banks, got {len(banks)}")
    clips, bank_audit, encoded = {}, [], {}
    for bank in sorted(banks):
        raw = archive_read(iso, bank)
        entries = media.xbb(raw)
        number = int(Path(bank).stem[2:])
        table_name = "RACEWORDS_" + Path(bank).stem.upper() + ".BIN"
        table = entries[table_name]
        count = struct.unpack_from("<H", table)[0]
        if len(table) != 2 + count * 60:
            raise ValueError(f"Unexpected race-word table: {bank}")
        events = {}
        for i in range(count):
            row = table[2 + 60 * i:2 + 60 * (i + 1)]
            name = row[28:].split(b"\0")[0].decode("ascii")
            if name not in entries:
                raise ValueError(f"Unresolved voice reference: {name}")
            events.setdefault(name, set()).add(row[1])
        non_audio = []
        for name, data in entries.items():
            if not name.lower().endswith(".at3"):
                non_audio.append(dict(name=name, bytes=len(data), sha256=digest(data)))
                continue
            sha = digest(data)
            encoded.setdefault(sha, data)
            clip = clips.setdefault(sha, dict(sourceSha256=sha, aliases=[]))
            clip["aliases"].append(dict(bank=number, id=Path(name).stem,
                                        events=sorted(events.get(name, []))))
        bank_audit.append(dict(path=bank, sha256=digest(raw), entries=len(entries),
                               raceWordRows=count, raceWordRowBytes=60,
                               nonAudioEntries=non_audio))

    # Inventory text-bearing containers without treating patched glyph codes as Japanese.
    containers = []
    for name in inventory:
        if not name.endswith(".bin.gz"):
            continue
        if any(part in name for part in ("/car/", "/course/", "/sound/")):
            continue
        raw = archive_read(iso, name)
        data = gzip.decompress(raw)
        if not data.startswith(b"XBB\x01"):
            continue
        entries = media.xbb(data)
        text = [dict(name=n, bytes=len(v), sha256=digest(v)) for n, v in entries.items()
                if Path(n).suffix.lower() in (".txt", ".csv", ".json", ".xml", ".srt", ".ass")]
        references = [n for n, v in entries.items()
                      if not n.lower().endswith((".gim", ".gmo", ".at3"))
                      and re.search(rb"B_[A-Za-z0-9_]+\.at3", v)]
        containers.append(dict(path=name, sha256=digest(raw), entries=len(entries),
                                textEntries=text, voiceReferenceEntries=references))
    executables = []
    for name in ("PSP_GAME/SYSDIR/EBOOT.BIN", "PSP_GAME/SYSDIR/BOOT.BIN"):
        raw = archive_read(iso, name)
        references = re.findall(rb"B_[A-Za-z0-9_]+\.at3", raw)
        executables.append(dict(path=name, sha256=digest(raw), bytes=len(raw),
                                elf=raw.startswith(b"\x7fELF"),
                                voiceReferenceCount=len(references),
                                voiceReferenceNames=sorted({r.decode("ascii") for r in references}),
                                patchSubtitleMarker=bool(b"voice_subtitle_instruction\0" in raw),
                                executed=False))
    loose_text = []
    for name in inventory:
        if Path(name).suffix.lower() not in (".txt", ".csv", ".json", ".srt", ".ass"):
            continue
        raw = archive_read(iso, name)
        loose_text.append(dict(path=name, sha256=digest(raw), bytes=len(raw),
                               voiceReferenceCount=len(re.findall(rb"B_[A-Za-z0-9_]+\.at3", raw))))
    audit = dict(version=1, sourceImageSha256=None, voiceBanks=bank_audit,
                 containerAudit=containers, executables=executables, looseTextFiles=loose_text,
                 conclusion="Voice banks contain encoded audio and fixed-width event/name tables, "
                            "not Japanese dialogue. No direct Japanese race-voice script was located "
                            "in the inspected text-bearing containers. Patched subtitle data and "
                            "glyph maps are not a Japanese transcript and are excluded from ASR.")
    with iso.open("rb") as stream:
        audit["sourceImageSha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
    save(args.output / "source-audit.json", audit)
    wavs = args.work / "wavs"
    wavs.mkdir(parents=True, exist_ok=True)

    def decode(item):
        sha, data = item
        target = wavs / (sha + ".wav")
        if not target.exists():
            with tempfile.NamedTemporaryFile(suffix=".at3", dir=args.work) as source:
                source.write(data)
                source.flush()
                subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-y", "-i", source.name,
                                "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", str(target)],
                               check=True, capture_output=True)
        with wave.open(str(target)) as decoded:
            if decoded.getnchannels() != 1 or decoded.getframerate() != 16000:
                raise ValueError(f"Unexpected PCM format: {target}")
            frames = decoded.getnframes()
            if not frames or not any(decoded.readframes(frames)):
                raise ValueError(f"Empty voice: {target}")
            return sha, frames / decoded.getframerate(), digest(target.read_bytes())

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        for sha, duration, wav_hash in pool.map(decode, encoded.items()):
            clips[sha].update(duration=duration, wavSha256=wav_hash)
    catalog = dict(version=1, sourceImageSha256=audit["sourceImageSha256"],
                   decoder="ffmpeg; original ATRAC3 -> mono 16000 Hz PCM s16le",
                   inputExcludes=["Chinese subtitle text", "patched font", "patched subtitle timings"],
                   uniqueAudio=len(clips), aliases=sum(len(c["aliases"]) for c in clips.values()),
                   audioSeconds=sum(c["duration"] for c in clips.values()),
                   clips=sorted(clips.values(), key=lambda c: (c["aliases"][0]["bank"],
                                                               c["aliases"][0]["id"])))
    save(args.output / "sources.json", catalog)
    print(json.dumps({k: catalog[k] for k in ("uniqueAudio", "aliases", "audioSeconds")}), flush=True)


def review_record(clip, raw):
    segments = raw.get("transcription", [])
    text = "".join(s.get("text", "") for s in segments).strip()
    tokens = [t for s in segments for t in s.get("tokens", [])
              if t.get("text", "").strip() and not t.get("text", "").startswith("[_")]
    probabilities = [t["p"] for t in tokens if isinstance(t.get("p"), (int, float))
                     and math.isfinite(t["p"]) and 0 <= t["p"] <= 1]
    score = math.exp(sum(math.log(max(p, 1e-12)) for p in probabilities) / len(probabilities)) \
        if probabilities else None
    flags = []
    if not text:
        flags.append("empty")
    if text and not re.search(r"[\u3040-\u30ff\u3400-\u9fff]", text):
        flags.append("no_japanese_characters")
    if score is None or score < 0.65:
        flags.append("low_token_probability")
    if any(p < 0.15 for p in probabilities):
        flags.append("very_low_probability_token")
    if len(text) > max(20, clip["duration"] * 16):
        flags.append("implausible_text_length")
    if any(name in text for name in ("凌介", "東道塾", "京市", "京一郎")):
        flags.append("suspect_proper_name")
    return dict(sourceSha256=clip["sourceSha256"], aliases=clip["aliases"],
                audioSeconds=clip["duration"], japaneseCandidate=text,
                tokenProbabilityGeometricMean=score, reviewFlags=flags,
                status="unreviewed_asr", chineseTranslation=None, segments=segments)


def trial_order(clips):
    groups = {}
    for clip in clips:
        groups.setdefault(clip["aliases"][0]["bank"], []).append(clip)
    ordered, seen = [], set()
    for i in range(max((len(v) for v in groups.values()), default=0)):
        for group in groups.values():
            if i < len(group) and group[i]["sourceSha256"] not in seen:
                ordered.append(group[i])
                seen.add(group[i]["sourceSha256"])
    return ordered


def read_asr(path):
    if not path.exists():
        return None
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeError, ValueError):
        return None
    if not isinstance(raw, dict) or not isinstance(raw.get("transcription"), list):
        return None
    params = raw.get("params", {})
    if not isinstance(params, dict) or params.get("language") != "ja" or params.get("translate") is not False:
        return None
    for segment in raw["transcription"]:
        if not isinstance(segment, dict) or not isinstance(segment.get("text"), str):
            return None
        tokens = segment.get("tokens")
        if not isinstance(tokens, list) or any(
                not isinstance(t, dict) or not isinstance(t.get("text"), str) for t in tokens):
            return None
    return raw


def transcribe(args):
    catalog = json.loads((args.output / "sources.json").read_text())
    # A limited trial includes one clip per opponent before taking second clips.
    ordered = trial_order(catalog["clips"])
    if args.review_from:
        review = json.loads(args.review_from.read_text())
        selected_hashes = {c["sourceSha256"] for c in review["clips"] if c["reviewFlags"]}
        ordered = [c for c in ordered if c["sourceSha256"] in selected_hashes]
    selected = ordered[:args.limit] if args.limit else ordered
    raw_dir = args.output / args.run / "raw"
    raw_dir.mkdir(parents=True, exist_ok=True)
    model_sha = digest(args.model.read_bytes())
    executable_sha = digest(args.whisper.read_bytes())
    options = ["-l", "ja", "-ojf", "-nt", "-np", "-bo", "5", "-bs", "5", "-t", "6"]
    run_config = dict(model=args.model.name, modelSha256=model_sha,
                      executableSha256=executable_sha, options=options,
                      noSubtitlePrompt=True, uploadAudio=False)
    config_path = raw_dir.parent / "run.json"
    if config_path.exists() and json.loads(config_path.read_text()) != run_config:
        raise ValueError("ASR configuration changed: choose a new --run name")
    save(config_path, run_config)
    pending = [c for c in selected if read_asr(raw_dir / (c["sourceSha256"] + ".json")) is None]
    for start in range(0, len(pending), args.batch):
        batch = pending[start:start + args.batch]
        command = [str(args.whisper.resolve()), "-m", str(args.model.resolve()), *options]
        for clip in batch:
            sha = clip["sourceSha256"]
            wav = args.work / "wavs" / (sha + ".wav")
            if digest(wav.read_bytes()) != clip["wavSha256"]:
                raise ValueError(f"ASR input changed: {sha}")
            command.extend(["-f", str(wav.resolve()), "-of", str((raw_dir / sha).resolve())])
        print(f"Transcribing {start + 1}-{start + len(batch)} / {len(pending)}", flush=True)
        with (raw_dir.parent / f"batch-{start:04}.log").open("a") as log:
            completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
        if completed.returncode:
            raise RuntimeError(f"ASR exited {completed.returncode}; see "
                               f"{raw_dir.parent / f'batch-{start:04}.log'}")
    rows = []
    for clip in catalog["clips"]:
        raw_file = raw_dir / (clip["sourceSha256"] + ".json")
        raw = read_asr(raw_file)
        if raw is not None:
            rows.append(review_record(clip, raw))
        elif clip in selected:
            raise ValueError(f"Missing or invalid ASR output: {raw_file}")
    result = dict(version=1, sourceImageSha256=catalog["sourceImageSha256"],
                  provenance=run_config, totalUniqueAudio=catalog["uniqueAudio"],
                  completed=len(rows), completedAliases=sum(len(c["aliases"]) for c in rows),
                  flagged=sum(bool(c["reviewFlags"]) for c in rows),
                  allRequireHumanReview=True,
                  probabilityNote="Token probabilities are model scores, not calibrated accuracy.",
                  clips=rows)
    save(raw_dir.parent / "japanese-candidates.json", result)
    print(json.dumps({k: result[k] for k in ("completed", "completedAliases", "flagged")}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    prep.add_argument("--iso", required=True, type=Path)
    prep.add_argument("--jobs", type=int, default=4)
    run = sub.add_parser("transcribe")
    run.add_argument("--whisper", required=True, type=Path)
    run.add_argument("--model", required=True, type=Path)
    run.add_argument("--run", default="turbo")
    run.add_argument("--limit", type=int, default=0)
    run.add_argument("--batch", type=int, default=32)
    run.add_argument("--review-from", type=Path, help="Recheck flagged clips from another ASR run")
    for item in (prep, run):
        item.add_argument("--output", type=Path, required=True)
        item.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "transcribe" and (args.batch < 1 or args.limit < 0):
        parser.error("batch must be positive and limit must be non-negative")
    {"prepare": prepare, "transcribe": transcribe}[args.command](args)


if __name__ == "__main__":
    main()
