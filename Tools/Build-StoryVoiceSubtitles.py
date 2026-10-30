#!/usr/bin/env python3
"""Compile independently authored bilingual subtitles for Street Stage speech."""
import argparse
import csv
import difflib
import hashlib
import json
from pathlib import Path
import re
import unicodedata

ROOT = Path(__file__).resolve().parents[1]
AUTHORING = ROOT / "Native/localization/story-voices"


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def catalog(args):
    raw = json.loads(args.asr.read_text())
    assert raw["completed"] == raw["totalUniqueAudio"] == 1190
    assert raw["completedAliases"] == 1331 and raw["provenance"]["noSubtitlePrompt"]
    rows = []
    for clip in raw["clips"]:
        alias = clip["aliases"][0]
        rows.append(dict(bank=alias["bank"], id=alias["id"], sourceSha256=clip["sourceSha256"],
                         duration=clip["audioSeconds"], japaneseAsr=clip["japaneseCandidate"],
                         reviewFlags=clip["reviewFlags"], aliases=clip["aliases"]))
    save(AUTHORING / "source.json", dict(
        version=1, sourceImageSha256=raw["sourceImageSha256"],
        asr=raw["provenance"], transcriptionStatus="ASR draft; not a verified verbatim transcript",
        clips=rows))


def translations(source, directory=AUTHORING):
    expected = {c["id"]: c for c in source["clips"]}
    result = {}
    for path in sorted(directory.glob("*.tsv")):
        with path.open(encoding="utf-8", newline="") as stream:
            for line, row in enumerate(csv.reader(stream, delimiter="\t", quoting=csv.QUOTE_NONE), 1):
                if not row or row[0].startswith("#"):
                    continue
                if len(row) != 3 or any(not text.strip() for text in row):
                    raise ValueError(f"{path.name}:{line}: expected id, Chinese, English")
                key, zh, en = row
                if key not in expected or key in result:
                    raise ValueError(f"{path.name}:{line}: unknown or duplicate id {key}")
                if expected[key]["bank"] != int(path.stem):
                    raise ValueError(f"{path.name}:{line}: wrong source bank")
                if not any("\u3400" <= c <= "\u9fff" for c in zh):
                    raise ValueError(f"{key}: missing Chinese translation")
                if any("\u3400" <= c <= "\u9fff" for c in en):
                    raise ValueError(f"{key}: English contains CJK text")
                result[key] = dict(zh=zh, en=en)
    missing = sorted(set(expected) - result.keys())
    if missing:
        raise ValueError(f"Missing {len(missing)} translations: {', '.join(missing[:8])}")
    return result


def review(args):
    source_path = AUTHORING / "source.json"
    source = json.loads(source_path.read_text())
    second = json.loads(args.asr.read_text())
    if second["completed"] != 1190 or second["sourceImageSha256"] != source["sourceImageSha256"]:
        raise ValueError("Second ASR must cover the same complete audio source")
    candidates = {c["sourceSha256"]: c for c in second["clips"]}
    rows = translations(source)
    def normalized(text):
        return "".join(c for c in unicodedata.normalize("NFKC", text) if c.isalnum())
    report = []
    for clip in source["clips"]:
        other = candidates[clip["sourceSha256"]]
        clip["japaneseReviewAsr"] = other["japaneseCandidate"]
        ratio = difflib.SequenceMatcher(None, normalized(clip["japaneseAsr"]),
                                       normalized(other["japaneseCandidate"])).ratio()
        if ratio < .8:
            report.append(dict(id=clip["id"], similarity=round(ratio, 3),
                               first=clip["japaneseAsr"], second=other["japaneseCandidate"],
                               **rows[clip["id"]]))
    source["reviewAsr"] = second["provenance"]
    save(source_path, source)
    print(json.dumps(sorted(report, key=lambda c: c["id"]), ensure_ascii=False, indent=2))


def wrap(text, advances, width=26):
    lines, line = [], ""
    closing = "，。！？：；、）》」』】,.!?:;)"
    for token in re.findall(r"[A-Za-z0-9][A-Za-z0-9'./_-]*| +|.", text):
        candidate = line + token
        size = sum(advances.get(c, .5) for c in candidate)
        if line and size > width and token[0] not in closing:
            lines.append(line.rstrip())
            line = token.lstrip()
        else:
            line = candidate
    if line.strip():
        lines.append(line.rstrip())
    if len(lines) > 7 or any(sum(advances.get(c, .5) for c in row) > width + 3 for row in lines):
        raise ValueError("Subtitle exceeds the lower screen area: " + text)
    return "\n".join(lines)


def build(output, media=None):
    from PIL import Image, ImageDraw, ImageFont
    from fontTools.ttLib import TTFont
    source = json.loads((AUTHORING / "source.json").read_text())
    rows = translations(source)
    font_path = ROOT / "Assets/Resources/Fonts/NotoSansSC-Regular.otf"
    font_source = TTFont(font_path)
    cmap = font_source.getBestCmap()
    characters = sorted(set("".join(t for r in rows.values() for t in r.values())) - {" "})
    missing = [c for c in characters if ord(c) not in cmap]
    if missing:
        raise ValueError("Font lacks subtitle glyphs: " + "".join(missing))
    font = ImageFont.truetype(str(font_path), 56)
    metrics = {c: round(font.getlength(c) / 56, 5) for c in characters}
    metrics[" "] = .5
    pages = (len(characters) + 255) // 256
    atlas = Image.new("RGBA", (1024, pages * 1024))
    draw = ImageDraw.Draw(atlas)
    indices = {c: i for i, c in enumerate(characters)}
    for c, i in indices.items():
        left, top, right, bottom = font.getbbox(c, anchor="ls")
        if left + 4 < 0 or top + 49 < 0 or right + 4 > 64 or bottom + 49 > 64:
            raise ValueError("Subtitle glyph would bleed across atlas cells: " + c)
        draw.text((i % 16 * 64 + 4, i // 16 * 64 + 49), c, font=font,
                  fill="white", anchor="ls", stroke_width=0)
    manifest = media or json.loads((output / "voices.json").read_text())
    authored = {}
    for clip in source["clips"]:
        row = rows[clip["id"]]
        for alias in clip["aliases"]:
            authored[(alias["bank"], alias["id"])] = (clip, row)
    used = set()
    for bank in manifest["banks"]:
        for clip in bank["clips"]:
            original, row = authored[(bank["id"], clip["id"])]
            if original["sourceSha256"] != clip["sourceSha256"]:
                raise ValueError("Audio provenance mismatch: " + clip["id"])
            clip["seconds"] = original["duration"]
            for language, field in (("zh", "glyphs"), ("en", "englishGlyphs")):
                text = wrap(row[language], metrics)
                clip[field] = [-1 if c == "\n" else -2 if c == " " else indices[c] for c in text]
            used.add((bank["id"], clip["id"]))
    if len(used) != 1331 or len(authored) != 1331:
        raise ValueError("Incomplete subtitle alias coverage")
    manifest = dict(version=2, source="Street Stage original speech; independently authored subtitles",
                    sourceImageSha256=source["sourceImageSha256"],
                    authoringSha256=hashlib.sha256(b"".join(p.read_bytes() for p in sorted(AUTHORING.glob("*.tsv")))).hexdigest(),
                    fontSha256=hashlib.sha256(font_path.read_bytes()).hexdigest(),
                    languages=["zh-Hans", "en"], atlasPages=pages,
                    advances=[metrics[c] for c in characters], banks=manifest["banks"])
    output.mkdir(parents=True, exist_ok=True)
    atlas.save(output / "subtitles.png", optimize=True)
    copyright_text = "\n".join(sorted({n.toUnicode() for n in font_source["name"].names if n.nameID == 0}))
    license_text = (ROOT / "Assets/Resources/Fonts/LICENSE.txt").read_text()
    (output / "SUBTITLE-FONT-LICENSE.txt").write_text(
        "Subtitle atlas generated from Noto Sans SC Regular.\n" + copyright_text + "\n\n" + license_text)
    save(output / "voices.json", manifest)
    print(json.dumps(dict(translations=len(rows), aliases=len(used), glyphs=len(characters),
                          atlasPages=pages, languages=manifest["languages"])))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    cat = sub.add_parser("catalog")
    cat.add_argument("--asr", type=Path, required=True)
    sub.add_parser("check")
    rev = sub.add_parser("review")
    rev.add_argument("--asr", type=Path, required=True)
    compile_parser = sub.add_parser("build")
    compile_parser.add_argument("--output", type=Path, default=ROOT / "Native/data/story_voices")
    args = parser.parse_args()
    if args.command == "catalog":
        catalog(args)
    elif args.command == "review":
        review(args)
    elif args.command == "build":
        build(args.output)
    else:
        source = json.loads((AUTHORING / "source.json").read_text())
        rows = translations(source)
        print(json.dumps(dict(translations=len(rows), languages=["zh-Hans", "en"])))


if __name__ == "__main__":
    main()
