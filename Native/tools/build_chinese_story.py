"""Compile independently translated D3 scripts and freshly rasterized fonts."""
import argparse
import hashlib
import json
import re
import struct
import unicodedata
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont
from chinese_ui_text import FONTS, font_reference, rival_names

ROOT = Path(__file__).resolve().parents[2]
REVIEW = ROOT / "Native/localization/zh-Hans"
OUTPUT = ROOT / "Native/data/localization/zh-Hans"
STORIES = REVIEW / "stories"
SOURCES = ("scripts.json", "bunta-scripts.json")


def read_dialogue(data):
    if data[:8] != b"IDASZH2\0" or struct.unpack_from("<I", data, 8)[0] != 2:
        raise ValueError("Expected the existing v2 display sidecar")
    rows, at = {}, 16
    for _ in range(struct.unpack_from("<I", data, 12)[0]):
        pool, index, count, playback_count = struct.unpack_from("<4I", data, at)
        at += 16
        playback = []
        for _ in range(playback_count):
            source_index = struct.unpack_from("<I", data, at)[0]
            at += 4
            fields = []
            for _ in range(2):
                size = struct.unpack_from("<I", data, at)[0]
                at += 4
                fields.append(data[at:at + size].decode("utf-8"))
                at += size
            playback.append((source_index, *fields))
        rows[pool, index] = (count, playback)
    if at != len(data):
        raise ValueError("Unexpected trailing dialogue data")
    return rows


def passages(tokens):
    at = 0
    while at < len(tokens):
        start = at
        if not tokens[at]["bytes"].startswith("*"):
            yield start, None
            at += 1
            continue
        group = []
        while at < len(tokens) and tokens[at]["bytes"].startswith("*"):
            group.append(tokens[at]["bytes"][1:])
            at += 1
        yield start, " ".join("".join(group).split())


def source_catalog():
    catalog, pools = {}, []
    for pool, filename in enumerate(SOURCES):
        source = ROOT / "Native/data/original_assets/rival_dialog" / filename
        records = json.loads(source.read_text())["records"]
        pools.append(records)
        groups = {}
        for record in records:
            key = f'{record["character"]:02}' if pool == 0 else "bunta"
            entries = groups.setdefault(key, {})
            for _, english in passages(record["tokens"]):
                if english is not None:
                    entries.setdefault(english, None)
        catalog[filename] = dict(sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                                groups={key: list(entries) for key, entries in groups.items()})
    return catalog, pools


def independent_rows():
    catalog, pools = source_catalog()
    if catalog != json.loads((REVIEW / "story-source.json").read_text()):
        raise ValueError("Arcade source changed: review translation alignment before rebuilding")
    miki = json.loads((REVIEW / "miki.json").read_text())["translations"]
    translations, hashes = {}, {}
    for document in catalog.values():
        for key, english in document["groups"].items():
            if key == "17":
                values = [miki[text] for text in english]
            else:
                path = STORIES / f"{key}.txt"
                values = path.read_text().splitlines()
                hashes[path.relative_to(ROOT).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
            if len(values) != len(english):
                raise ValueError(f"{key}: expected {len(english)} translations, got {len(values)}")
            for original, text in zip(english, values, strict=True):
                if (text == "<empty>") != (original == ""):
                    raise ValueError(f"{key}: only blank source tokens may be omitted")
                if not text.strip() or any(0xe000 <= ord(c) <= 0xf8ff for c in text):
                    raise ValueError(f"{key}: empty or legacy private-use translation")
            translations[key] = dict(zip(english, (v.replace("\\n", "\n") for v in values), strict=True))
    rows = {}
    for pool, records in enumerate(pools):
        for index, record in enumerate(records):
            key = f'{record["character"]:02}' if pool == 0 else "bunta"
            playback = [(at, "", "") if english is None else
                        (0xffffffff, "*", translations[key][english])
                        for at, english in passages(record["tokens"]) if english != ""]
            rows[pool, index] = (len(record["tokens"]), playback)
    hashes["Native/localization/zh-Hans/miki.json"] = hashlib.sha256((REVIEW / "miki.json").read_bytes()).hexdigest()
    return rows, dict(records=len(rows), independent_records=len(rows),
                      review_entries=sum(len(v) for v in translations.values()),
                      unique_source_passages=len(set().union(*(v.keys() for v in translations.values()))),
                      retained_legacy_records=0,
                      sources={name: value["sha256"] for name, value in catalog.items()},
                      translations=hashes)


def write_dialogue(rows):
    data = bytearray(b"IDASZH2\0") + struct.pack("<2I", 2, len(rows))
    for (pool, index), (count, playback) in sorted(rows.items()):
        data += struct.pack("<4I", pool, index, count, len(playback))
        for source_index, command, text in playback:
            data += struct.pack("<I", source_index)
            for field in (command, text):
                raw = field.encode("utf-8")
                data += struct.pack("<I", len(raw)) + raw
    (OUTPUT / "dialog.bin").write_bytes(data)


def independent_labels():
    glyphs = {}
    for base, role, height in ((0xf100, "body", 12), (0xf200, "brush", 32)):
        font = ImageFont.truetype(str(FONTS[role]), 96)
        for rival, name in enumerate(rival_names()):
            white = 3 if base == 0xf100 else 0
            border = 4 if base == 0xf100 else 3
            box = font.getbbox(name, anchor="lt", stroke_width=white + border)
            image = Image.new("RGBA", (box[2] - box[0] + 8, box[3] - box[1] + 8))
            draw, position = ImageDraw.Draw(image), (4 - box[0], 4 - box[1])
            draw.text(position, name, font=font, anchor="lt", fill="#13151a",
                      stroke_width=white + border, stroke_fill="#13151a")
            draw.text(position, name, font=font, anchor="lt", fill="white",
                      stroke_width=white, stroke_fill="white")
            image = image.crop(image.getbbox())
            width = max(1, round(image.width * height / image.height))
            if base == 0xf100:
                width = min(width, 62)
            glyphs[base + rival] = (image.resize((width, height), Image.Resampling.LANCZOS), width)
    return glyphs


def update_unicode_font(rows):
    glyphs = independent_labels()
    needed = set(map(chr, range(32, 127)))
    for _, playback in rows.values():
        for _, _, text in playback:
            needed.update(text)
    for name in rival_names():
        needed.update(name)
    for entry in json.loads((REVIEW / "names.json").read_text())["characters"]:
        needed.update(entry["zh"] + entry["dialogue"])
    needed.update("胖子眼镜")
    source = (ROOT / "Native/src/original_rival_dialog_tables.inc").read_text()
    for text in re.findall(r'"((?:\\x[0-9a-fA-F]{2})+)"', source):
        needed.update(unicodedata.normalize(
            "NFKC", bytes.fromhex(text.replace("\\x", "")).decode("euc_jp")))
    needed.add("：")
    needed.discard("\n")
    fonts = {role: ImageFont.truetype(str(FONTS[role]), 14 * 4)
             for role in ("body", "caption")}
    for char in sorted(needed):
        equivalent = {"−": "-", "’": "'"}.get(char, char)
        font = fonts["body" if ord(equivalent) < 128 else "caption"]
        cell = Image.new("RGBA", (16 * 4, 16 * 4))
        ImageDraw.Draw(cell).text((0, 13 * 4), equivalent, font=font, anchor="ls", fill="white")
        cell = cell.resize((16, 16), Image.Resampling.LANCZOS)
        advance = 16
        if ord(equivalent) < 128:
            mask = ImageChops.multiply(cell.convert("L"), cell.getchannel("A")).point(
                lambda value: 255 if value > 127 else 0)
            box = mask.getbbox()
            if box:
                normalized = Image.new("RGBA", (16, 16))
                normalized.alpha_composite(cell.crop((box[0], 0, box[2], 16)))
                cell, advance = normalized, box[2] - box[0] + 1
            elif equivalent == " ":
                advance = 5
            else:
                raise ValueError(f"Empty independent glyph: {char!r}")
        glyphs[ord(char)] = (cell, advance)
    atlas = Image.new("RGBA", (512, 2048))
    records, x, y, row_height = {}, 0, 0, 0
    for code, (image, advance) in sorted(glyphs.items()):
        if x + image.width > atlas.width:
            x, y, row_height = 0, y + row_height + 1, 0
        if y + image.height > atlas.height:
            raise ValueError("Shared font exceeds the production atlas limit")
        atlas.alpha_composite(image, (x, y))
        records[code] = (x, y, image.width, image.height, advance)
        x += image.width + 1
        row_height = max(row_height, image.height)
    data = bytearray(b"IDMF") + struct.pack("<5I", 1, atlas.width, atlas.height, 16, len(records))
    for code, metrics in sorted(records.items()):
        data += struct.pack("<I5f", code, *metrics)
    data += atlas.tobytes("raw", "BGRA")
    (OUTPUT / "font.bin").write_bytes(data)
    return len(needed), sum(0xe000 <= code < 0xf100 for code in glyphs)


def build():
    rows, report = independent_rows()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    write_dialogue(rows)
    report["independent_unicode_glyphs"], report["retained_legacy_glyph_slots"] = update_unicode_font(rows)
    report["fonts"] = {role: dict(file=font_reference(FONTS[role]),
                                 sha256=hashlib.sha256(FONTS[role].read_bytes()).hexdigest())
                       for role in ("caption", "body")}
    report["name_label_font"] = dict(file=font_reference(FONTS["brush"]),
                                    sha256=hashlib.sha256(FONTS["brush"].read_bytes()).hexdigest())
    (REVIEW / "story-build.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"{len(rows)} independently translated records; no retained legacy rows or glyphs")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("catalog", "build"), nargs="?", default="build")
    args = parser.parse_args()
    if args.action == "catalog":
        (REVIEW / "story-source.json").write_text(json.dumps(source_catalog()[0], ensure_ascii=False, indent=2) + "\n")
    else:
        build()
