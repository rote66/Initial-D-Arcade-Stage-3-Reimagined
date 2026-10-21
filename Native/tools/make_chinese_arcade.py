"""Typeset Chinese display text inside the arcade's original geometry."""
import argparse
import copy
from functools import lru_cache
import hashlib
import json
import struct
import unicodedata
import re
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps
from chinese_ui_text import FONTS, GLYPH_ALIASES, IMPORTED_COURSES, font_reference, specs as text_specs
from import_japanese_ui import read_mesh, read_textures, write_textures
from extract_original_models import write_binary

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT/"Native/data/original_assets"
REVIEW = ROOT/"Native/localization/zh-Hans"
OUTPUT = ROOT/"Native/data/localization/zh-Hans"


def source_layer(selected, textures, extent, size):
    """Measure ink through the authored triangles, including mirrored UVs."""
    left, bottom, right, top = extent
    width, height = size
    image = Image.new("RGBA", size)
    for batch in selected:
        texture = batch["material"][9]
        if texture == 0xffffffff:
            continue
        tw, th, rgba = textures[texture]
        pixels = np.frombuffer(rgba, dtype=np.uint8).reshape(th, tw, 4)
        for first in range(0, len(batch["indices"]), 3):
            points = [batch["vertices"][i] for i in batch["indices"][first:first+3]]
            if len(points) != 3:
                continue
            xy = np.array([[(v[1]-left)*width/(right-left),
                            (top-v[2])*height/(top-bottom)] for v in points])
            a = np.column_stack((xy, np.ones(3)))
            if abs(np.linalg.det(a)) < 1e-8:
                continue
            x0, y0 = np.maximum(np.floor(xy.min(axis=0)).astype(int), 0)
            x1, y1 = np.minimum(np.ceil(xy.max(axis=0)).astype(int), size)
            if x1 <= x0 or y1 <= y0:
                continue
            xx, yy = np.meshgrid(np.arange(x0, x1)+.5, np.arange(y0, y1)+.5)
            weights = np.stack((xx, yy, np.ones_like(xx)), axis=-1) @ np.linalg.inv(a)
            mask = np.all(weights >= -1e-6, axis=-1)
            uv = weights @ np.array([[v[7], v[8]] for v in points])
            tsp = batch["words"][2]
            for axis, clamp, mirror in ((0, 16, 18), (1, 15, 17)):
                if tsp & (1 << clamp):
                    uv[:, :, axis] = np.clip(uv[:, :, axis], 0, 1-1e-7)
                else:
                    base = np.floor(uv[:, :, axis])
                    uv[:, :, axis] -= base
                    if tsp & (1 << mirror):
                        uv[:, :, axis] = np.where(base.astype(int) % 2,
                                                  1-uv[:, :, axis], uv[:, :, axis])
            sample = pixels[np.clip((uv[:, :, 1]*th).astype(int), 0, th-1),
                            np.clip((uv[:, :, 0]*tw).astype(int), 0, tw-1)].copy()
            sample[~mask, 3] = 0
            image.alpha_composite(Image.fromarray(sample), (x0, y0))
    return image


def place_ink(canvas, ink, box):
    x0, y0, x1, y1 = box
    scale = min((x1-x0)/ink.width, (y1-y0)/ink.height)
    ink = ink.resize((max(1, round(ink.width*scale)), max(1, round(ink.height*scale))),
                     Image.Resampling.LANCZOS)
    canvas.alpha_composite(ink, (round((x0+x1-ink.width)/2), round((y0+y1-ink.height)/2)))


@lru_cache
def face(role):
    return ImageFont.truetype(str(FONTS[role]), 96)


def lettering(text, role="label", color="white", italic=False, metallic=False,
              accent="", stroke=None, tracking=0, shadow=False, fixed_baseline=False):
    text = text.translate(str.maketrans(GLYPH_ALIASES.get(role, {})))
    font = face(role)
    padding = 24
    image = Image.new("RGBA", (max(1, round(font.getlength(text)+
                              max(0, len(text)-1)*tracking*96)+padding*2), 160))
    mask = Image.new("L", image.size)
    accent_mask = Image.new("L", image.size)
    draw, accent_draw = ImageDraw.Draw(mask), ImageDraw.Draw(accent_mask)
    x = padding
    y = padding if fixed_baseline else padding-font.getbbox(text, anchor="la")[1]
    for char in text:
        draw.text((x, y), char, font=font, anchor="la", fill=255)
        if char in accent:
            accent_draw.text((x, y), char, font=font, anchor="la", fill=255)
        x += font.getlength(char)+tracking*96
    if role in ("latin", "title"):
        dilation = 7 if role == "latin" else 3
        mask = mask.filter(ImageFilter.MaxFilter(dilation))
        accent_mask = accent_mask.filter(ImageFilter.MaxFilter(dilation))
    if metallic:
        pixels = np.zeros((image.height, image.width, 4), dtype=np.uint8)
        for y in range(image.height):
            t = y/max(1, image.height-1)
            value = round(255-85*t) if t < .62 else round(128+80*(t-.62)/.38)
            pixels[y, :, :3] = (min(255, value), min(255, value+4), min(255, value+14))
        ink = Image.fromarray(pixels)
        ink.putalpha(mask)
    else:
        ink = Image.new("RGBA", image.size, color)
        ink.putalpha(mask)
    if shadow:
        shade = Image.new("RGBA", image.size, "black")
        shifted = Image.new("L", image.size)
        shifted.paste(mask, (4, 5))
        shade.putalpha(shifted)
        image.alpha_composite(shade)
    edge = stroke or ("#09090b" if role == "mode" else "#15151b"
                     if role not in ("body", "brush", "title", "caption") else None)
    if edge:
        outline = Image.new("RGBA", image.size, edge)
        outline.putalpha(mask.filter(ImageFilter.MaxFilter(
            15 if role == "mode" else 5 if role == "body" else 9)))
        image.alpha_composite(outline)
    if accent:
        outline = Image.new("RGBA", image.size, "#ff1414")
        outline.putalpha(accent_mask.filter(ImageFilter.MaxFilter(19)))
        image.alpha_composite(outline)
    image.alpha_composite(ink)
    if italic:
        skew = .15 if role == "mode" else .20
        image = image.transform(
            (image.width+round(image.height*skew), image.height),
            Image.Transform.AFFINE, (1, skew, -image.height*skew, 0, 1, 0),
            Image.Resampling.BICUBIC)
    box = image.getbbox() or (0, 0, 1, 1)
    if fixed_baseline:
        box = (box[0], 12, box[2], 140)
    return image.crop(box)


def hint_lettering(controls):
    """D3's 70x16 control boxes, yellow actions and 30px separators."""
    scale = 4
    font = ImageFont.truetype(str(FONTS["body"]), 11*scale)
    slots = [max(55 if i == 0 else 24, int(np.ceil(font.getlength(row["action"])/scale))+2)
             for i, row in enumerate(controls)]
    width = sum(76+slot for slot in slots)+16*(len(controls)-1)
    image = Image.new("RGBA", (width*scale, 32*scale))
    draw = ImageDraw.Draw(image)
    x = 0
    for i, (row, slot) in enumerate(zip(controls, slots)):
        if i:
            draw.line(((x+6)*scale, scale, (x+6)*scale, 32*scale-1),
                      fill="white", width=scale)
            x += 16
        draw.rectangle((x*scale, 8*scale, (x+70)*scale, 24*scale),
                       outline="white", width=scale)
        control_width = font.getlength(row["control"])
        if control_width > 62*scale:
            raise ValueError(f"D3 control label exceeds its frame: {row['control']}")
        draw.text(((x+35)*scale-control_width/2, 21*scale), row["control"],
                  font=font, anchor="ls", fill="white", stroke_width=1)
        draw.text(((x+76)*scale, 21*scale), row["action"], font=font, anchor="ls",
                  fill="#ffbb00", stroke_width=1)
        x += 76+slot
    return image.crop(image.getbbox())


def brush_halo(ink, color):
    alpha = Image.new("L", (ink.width+64, ink.height+64))
    alpha.paste(ink.getchannel("A"), (32, 32))
    halo = Image.new("RGBA", alpha.size, color)
    halo.putalpha(alpha.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.GaussianBlur(5)))
    dark = Image.new("RGBA", alpha.size, "#101014")
    dark.putalpha(alpha)
    halo.alpha_composite(dark)
    return halo


def race_title_lettering(text, snow=False):
    """Independent D3-style navy/cyan ink, white keyline and blue light."""
    font = face("brush")
    mask = Image.new("L", (round(font.getlength(text))+64, 176))
    draw = ImageDraw.Draw(mask)
    draw.text((32, 32-font.getbbox(text, anchor="la")[1]), text,
              font=font, anchor="la", fill=255)
    box = mask.getbbox()
    if not box:
        raise ValueError("Empty race title")
    mask = mask.crop((box[0]-20, box[1]-20, box[2]+20, box[3]+20))
    outline = mask.filter(ImageFilter.MaxFilter(7))
    image = Image.new("RGBA", mask.size, "#006aff")
    image.putalpha(outline.filter(ImageFilter.GaussianBlur(8)))
    keyline = Image.new("RGBA", mask.size, "white")
    keyline.putalpha(outline)
    image.alpha_composite(keyline)
    ink = Image.new("RGBA", mask.size)
    pixels = np.array(ink)
    for y in range(mask.height):
        t = np.clip((y-20)/max(1, mask.height-40), 0, 1)
        pixels[y, :, :3] = ((8, 9, 18) if snow else
                            (round(2+9*t), round(12+132*t**4), round(62+125*t**4)))
    ink = Image.fromarray(pixels)
    ink.putalpha(mask)
    image.alpha_composite(ink)
    return image


def start_condition_lettering(text):
    scale = 4
    font = ImageFont.truetype(str(FONTS["caption"]), 22*scale)
    image = Image.new("RGBA", (round(font.getlength(text))+8*scale, 24*scale))
    ImageDraw.Draw(image).text((3*scale, 21*scale), text, font=font, anchor="ls",
                              fill="white", stroke_width=scale, stroke_fill="#09090b")
    return image.transform(image.size, Image.Transform.AFFINE,
                           (1, .2, -3*scale, 0, 1, 0), Image.Resampling.BICUBIC)


def course_title_lettering(text):
    ink = lettering(text, "brush")
    # Two antialiasing rows surround the original names' 41px dark strokes.
    scale = 43/ink.height
    title = brush_halo(ink, "white")
    title = title.resize((round(title.width*scale), round(title.height*scale)),
                         Image.Resampling.LANCZOS)
    return title, round(32*scale)


def course_title_artwork(text, snow=False):
    image = Image.new("RGBA", (398, 158))
    title, padding = course_title_lettering(text)
    if not snow:
        image.alpha_composite(title, (round((398-title.width)/2), 48-padding))
        return image
    # Keep Snow's separate tilted black badge, with an upright white glyph.
    plate = Image.new("RGBA", (42*4, 42*4), "black").rotate(
        28, resample=Image.Resampling.BICUBIC, expand=True)
    alpha = Image.new("L", (plate.width+48, plate.height+48))
    alpha.paste(plate.getchannel("A"), (24, 24))
    badge = Image.new("RGBA", alpha.size, "white")
    badge.putalpha(alpha.filter(ImageFilter.GaussianBlur(8)))
    badge.alpha_composite(plate, (24, 24))
    place_ink(badge, lettering("雪", "brush"), (80, 70, 220, 210))
    badge = badge.resize((round(badge.width/4), round(badge.height/4)),
                         Image.Resampling.LANCZOS)
    name_width = title.width-2*padding
    left = round((398-name_width-8-badge.width)/2)
    image.alpha_composite(title, (left-padding, 48-padding))
    image.alpha_composite(badge, (left+name_width+8, 33))
    return image


def course_tile(photo, name, level, snow=False):
    tile = ImageOps.fit(photo, (384, 256), method=Image.Resampling.LANCZOS)
    color = {"初级":"#1551d8", "中级":"#368c36",
             "高级":"#da6b16", "超高级":"#b83434"}[level]
    badge_width = 60 if len(level) == 3 else 48
    ImageDraw.Draw(tile).rectangle((0, 0, badge_width*4, 18*4), fill=color)
    place_ink(tile, lettering(level, "body"), (8, 5, (badge_width-2)*4, 17*4))
    label = lettering(name, "brush")
    label = label.resize((min(87*4, round(label.width*22*4/label.height)), 22*4),
                         Image.Resampling.LANCZOS)
    tile.alpha_composite(label, (round((384-label.width)/2), (25 if snow else 35)*4))
    if snow:
        ImageDraw.Draw(tile).rectangle((44*4, 48*4, 384, 256), fill="black")
        place_ink(tile, lettering("雪", "brush"), (57*4, 49*4, 83*4, 63*4))
    return tile


def mode_description(spec, extent, size):
    # Font em size and baselines are in arcade pixels, never an ink bounding box.
    scale = 4
    native_height = (extent[3]-extent[1])*100
    native_width = native_height*size[0]/size[1]
    canvas = Image.new("RGBA", (round(native_width*scale), round(native_height*scale)))
    ink = Image.new("RGBA", canvas.size)
    font = ImageFont.truetype(str(FONTS[spec["role"]]), round(spec["font_size"]*scale))
    draw = ImageDraw.Draw(ink)
    for row, line in enumerate(spec["text"].split("\n")):
        draw.text((0, (spec["paragraph_baseline"]+row*spec["line_pitch"])*scale),
                  line, font=font, anchor="ls", fill="white")
    ink = ink.resize((round(ink.width*spec.get("horizontal_scale", 1)), ink.height),
                     Image.Resampling.LANCZOS)
    left = round(spec["paragraph_left"]*scale)
    box = ink.getbbox()
    if box and (left+box[2] > canvas.width or box[3] > canvas.height):
        raise ValueError("Mode description exceeds its fixed-size paragraph")
    canvas.alpha_composite(ink, (left, 0))
    return canvas.resize(size, Image.Resampling.LANCZOS)


def render_text(spec, original, extent, size):
    if spec.get("course_title"):
        return course_title_artwork(spec["text"], spec.get("snow", False))
    if "paragraph_baseline" in spec:
        return mode_description(spec, extent, size)
    width, height = size
    box = original.getbbox() or (0, 0, width, height)
    if "paragraph_left" in spec or spec.get("fixed_baseline"):
        box = (0, 0, width, height)
    image = Image.new("RGBA", size)
    if spec.get("start_title"):
        x0, y0, x1, y1 = box
        if spec.get("snow_intro"):
            name_right = x0+(x1-x0)*.57
            place_ink(image, race_title_lettering(spec["text"]),
                      (x0, y0, name_right, y1))
            place_ink(image, race_title_lettering("雪", snow=True),
                      (x0+(x1-x0)*.52, y0, x0+(x1-x0)*.82, y1))
        else:
            place_ink(image, race_title_lettering(spec["text"]), box)
        return image
    if spec.get("start_condition"):
        return start_condition_lettering(spec["text"]).resize(size, Image.Resampling.LANCZOS)
    if spec.get("input_hint"):
        place_ink(image, hint_lettering(spec["controls"]), box)
        return image
    if spec.get("unavailable"):
        image = Image.new("RGBA", size, "black")
        box = (width*.08, height*.26, width*.92, height*.74)
    if spec.get("preserve_primary"):
        primary = original.getbbox()
        primary = (primary[0], round(primary[1]+(primary[3]-primary[1])*spec.get("trim_primary_top", 0)),
                   primary[2], primary[3])
        place_ink(image, original.crop(primary),
                  (box[0], box[1], box[2], box[1]+(box[3]-box[1])*.77))
        place_ink(image, lettering(spec["subtitle"], italic=True),
                  (box[0], box[1]+(box[3]-box[1])*.79, box[2], box[3]))
        return image
    if spec.get("course_card"):
        raise ValueError("Course cards must use their native photo layer")
    if spec.get("badge"):
        badge = Image.new("RGBA", (400, 80))
        draw = ImageDraw.Draw(badge)
        draw.polygon(((36, 3), (364, 3), (397, 40), (364, 77), (36, 77), (3, 40)),
                     fill="#253759", outline="#7c8cae", width=5)
        place_ink(badge, lettering(spec["text"], "body"), (70, 14, 330, 66))
        place_ink(image, badge, box)
        return image
    lines = spec["text"].split("\n")
    native_scale = height/((extent[3]-extent[1])*100)
    line_height = ((box[3]-box[1])/len(lines) if not spec.get("pixel_height")
                   else spec["pixel_height"]*native_scale)
    line_height = min(line_height, (box[3]-box[1])/len(lines))
    gap = native_scale*spec.get("line_gap", 2) if len(lines) > 1 else 0
    y = (box[1]+box[3]-line_height*len(lines)-gap*(len(lines)-1))/2
    y += native_scale*spec.get("vertical_offset", 0)
    inks = []
    for line in lines:
        ink = lettering(line, spec.get("role", "label"), spec.get("color", "white"),
                        spec.get("italic", False), spec.get("metallic", False),
                        spec.get("accent", ""), spec.get("stroke"),
                        spec.get("tracking", 0), spec.get("shadow", False),
                        spec.get("fixed_baseline", False))
        if spec.get("ink_aspect"):
            ink = ink.resize((round(ink.height*spec["ink_aspect"]), ink.height),
                             Image.Resampling.LANCZOS)
        if spec.get("horizontal_scale"):
            ink = ink.resize((round(ink.width*spec["horizontal_scale"]), ink.height),
                             Image.Resampling.LANCZOS)
        if spec.get("brush_glow"):
            ink = brush_halo(ink, spec.get("glow_color", "white"))
        inks.append(ink)
    paragraph_width = min(box[2]-box[0], max(ink.width/ink.height for ink in inks)*line_height)
    for ink in inks:
        x0, x1 = box[0], box[2]
        if spec.get("paragraph_align") == "left":
            x0 = (spec["paragraph_left"]*native_scale if "paragraph_left" in spec
                  else (box[0]+box[2]-paragraph_width)/2)
            x1 = x0+min(paragraph_width, ink.width/ink.height*line_height)
        place_ink(image, ink, (x0, y, x1, y+line_height))
        y += line_height+gap
    if spec.get("subtitle"):
        image = Image.new("RGBA", size)
        primary = lettering(spec["text"], spec.get("role", "label"),
                            metallic=True, italic=True)
        if spec.get("ink_aspect"):
            primary = primary.resize((round(primary.height*spec["ink_aspect"]), primary.height),
                                     Image.Resampling.LANCZOS)
        place_ink(image, primary,
                  (box[0], box[1], box[2], box[1]+(box[3]-box[1])*.70))
        place_ink(image, lettering(spec["subtitle"], italic=True),
                  (box[0], box[1]+(box[3]-box[1])*.78, box[2], box[3]))
    return image


def course_card(spec, index, size):
    path = SOURCE/"menus/v3/v3sK00common/v3sK00common.idasmesh"
    chunks = read_mesh(path)
    textures = read_textures(path.parent/"textures/textures.idastex")
    source_index = {15:20, 16:21, 17:22, 18:23, 19:24, 20:25, 22:27, 23:28, 24:29}[index]
    selected = chunks[source_index]["batches"]
    points = [v for batch in selected for v in batch["vertices"]]
    extent = (min(v[1] for v in points), min(v[2] for v in points),
              max(v[1] for v in points), max(v[2] for v in points))
    photo = source_layer(selected, textures, extent, (384, 256))
    return course_tile(photo, spec["text"], spec["course_card"],
                       spec.get("snow", False)).resize(size, Image.Resampling.LANCZOS)


def imported_course_artwork():
    """Re-typeset the extension cards without touching their source banks."""
    titles, audit = [], []
    for folder, name, level in IMPORTED_COURSES:
        path = ROOT/"RuntimeAssets"/folder/"menu.idastex"
        source = read_textures(path)
        original = Image.frombytes("RGBA", source[0][:2], source[0][2])
        preview = path.parent/"thumbnail-preview.png"
        # Old extension exports flattened their English titles into the photo.
        # Reframe an unlettered portion of the same course across the whole
        # background; maps are redrawn from the existing timed road markers.
        photo = (Image.open(preview).convert("RGBA") if preview.is_file()
                 else original.crop((0, 128, 398, 312)))
        card = ImageOps.fit(photo, (640, 312), method=Image.Resampling.LANCZOS)
        title_height = 41
        card.alpha_composite(course_title_artwork(name))
        ImageDraw.Draw(card).line((0, 117, 397, 117), fill="white")
        if folder in ("MYOGI_SPECIAL", "USUI_SPECIAL"):
            caption = lettering("SPECIAL STAGE", "body", stroke="black")
            place_ink(card, caption, (119, 99, 279, 112))
        course_map = imported_course_map(path.parent)
        card.alpha_composite(course_map)
        tile = course_tile(photo, name, level)
        tile = tile.resize((96, 64), Image.Resampling.LANCZOS)
        target = OUTPUT/"imported_courses"/folder/"menu.idastex"
        write_textures(target, [(image.width, image.height, image.tobytes()) for image in (card, tile)])
        intro = race_title_lettering(name)
        intro = intro.crop(intro.getbbox())
        titles.append((intro.width, intro.height, intro.tobytes()))
        audit.append(dict(folder=folder, name=name, level=level,
                          title_ink_height=title_height, tile_ink_height=22,
                          title_style="black-brush-white-halo",
                          title_center_x=199, title_ink_top=48,
                          tile_text_box=[4.5, 35, 91.5, 57], tile_center_x=48,
                          source_sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    write_textures(OUTPUT/"original_assets/hud/imported_start_names/titles.idastex", titles)
    (REVIEW/"imported-courses.json").write_text(json.dumps(dict(
        notes="Display names supplied by the user on 2026-10-19. GUNSAI uses the requested "
              "short name 群赛. These are display aliases only: stable course IDs, handling, "
              "route data and leaderboard identities are unchanged. Map labels retain source wording.",
        sources=["User-approved translations, 2026-10-19"],
        courses=audit), ensure_ascii=False, indent=2)+"\n")
    return len(audit)


def imported_course_map(pack):
    raw = (pack/"road.bin").read_bytes()
    count = struct.unpack_from("<I", raw, 4)[0]
    road = np.frombuffer(raw, "<f4", count*3, 8).reshape(-1, 3)
    manifest_path = pack/("scene.json" if (pack/"scene.json").is_file() else "manifest.json")
    manifest = json.loads(manifest_path.read_text())
    if "raceMarkers" in manifest:
        first, last = (manifest["raceMarkers"][0][key] for key in ("startIndex", "goalIndex"))
    elif "checkpoints" in manifest:
        first, last = manifest["checkpoints"][0], manifest["checkpoints"][-1]
    elif pack.name == "GUNSAI":
        first, last = 93, 3094
    elif pack.name == "ODAWARA":
        first, last = 0, count-1
    else:
        raise ValueError(f"Missing timed menu-map markers: {pack}")
    points = road[list(range(first, last, 4))+[last]][:, [0, 2]].astype(float)
    if "raceMarkers" not in manifest:
        points *= -1
    low, high = points.min(0), points.max(0)
    scale = min(165/(high[0]-low[0]), 228/(high[1]-low[1]))
    line = [(round((435+(x-low[0])*scale)*4), round((42+(y-low[1])*scale)*4))
            for x, y in points]
    overlay = Image.new("RGBA", (640*4, 312*4))
    draw = ImageDraw.Draw(overlay)
    draw.line(line, fill="#111111", width=5*4, joint="curve")
    draw.line(line, fill="white", width=2*4, joint="curve")
    markers = (line[0],) if pack.name == "ODAWARA" else (line[0], line[-1])
    labels = (("START",) if pack.name == "ODAWARA" else
              ("OUTBOUND", "INBOUND") if pack.name == "GUNSAI" else ("DOWNHILL", "UPHILL"))
    for index, ((x, y), text) in enumerate(zip(markers, labels)):
        draw.ellipse((x-3*4, y-3*4, x+3*4, y+3*4), fill="white")
        ink = lettering(text, "body", italic=True, stroke="black")
        ink = ink.resize((round(ink.width*14*4/ink.height), 14*4), Image.Resampling.LANCZOS)
        left = max(398*4, min(640*4-ink.width-4, x-ink.width//2))
        top = max(4, min(292*4-ink.height, y-20*4 if index == 0 else y+8*4))
        overlay.alpha_composite(ink, (left, top))
    if "raceMarkers" in manifest and 1000*scale <= 165:
        bar = 1000*scale
        draw.line((435*4, 292*4, round((435+bar)*4), 292*4), fill="white", width=4)
        place_ink(overlay, lettering("1 km", "body", italic=True, stroke="black"),
                  (450*4, 295*4, 490*4, 309*4))
    return overlay.resize((640, 312), Image.Resampling.LANCZOS)


def name_tables():
    entries = json.loads((REVIEW/"names.json").read_text())["characters"]
    names = {entry["en"]:entry["zh"] for entry in entries}
    dialogue = {entry["en"]:entry["dialogue"] for entry in entries}
    dialogue["OTAKU 1"], dialogue["OTAKU 2"] = "胖子", "眼镜"
    source = (ROOT/"Native/src/original_rival_dialog_tables.inc").read_text()
    section = source[source.index("speakerNameStrings"):source.index("speakerNameLists")]
    speakers = [unicodedata.normalize("NFKC", bytes.fromhex(s.replace("\\x", "")).decode("euc_jp"))
                for s in re.findall(r'"((?:\\x[0-9a-fA-F]{2})+)"', section)]
    rivals = ["IGGY","KENJI","SHINGO","TORU","KAWAI","MAYA","OTAKU 1","DANNY",
              "K.T.","COLE","ZACK","KYLE","RY","TAK","HAWK","KYLIE","CAINE","MIKI",
              "DICE","SMILEY","TOUCH","NOBU","SID","AKI","KYLIE","RY","EVO.V","EVO.VI",
              "K.T.","TAK","BUNTA","BUNTA"]
    rival_names = [names[x] for x in rivals]
    rival_names[5], rival_names[6] = "真子与沙雪", "东京来的两人"
    jis = {}
    for text in re.findall(r'"((?:\\x[0-9a-fA-F]{2})+)"', source):
        raw = bytes.fromhex(text.replace("\\x", ""))
        for n in range(0, len(raw)-1, 2):
            try:
                decoded = unicodedata.normalize("NFKC", raw[n:n+2].decode("euc_jp"))
            except UnicodeDecodeError:
                continue
            if len(decoded) == 1:
                jis[int.from_bytes(raw[n:n+2], "big")] = ord(decoded)
    def literal(text):
        return '"'+"".join(f"\\x{b:02x}" for b in text.encode("utf-8"))+'"'
    header = "// Generated by make_chinese_arcade.py from names and original JIS tables.\n"
    for key, values in (("chineseSpeakerNames", [dialogue[s] for s in speakers]),
                        ("chineseRivalNames", rival_names)):
        header += f"inline constexpr const char* {key}[]={{\n"
        header += ",\n".join(literal(x) for x in values)+"\n};\n"
    header += "inline constexpr std::pair<std::uint16_t,std::uint32_t> chineseJisUnicode[]={\n"
    header += ",\n".join(f"{{0x{k:04x},0x{v:x}}}" for k,v in sorted(jis.items()))+"\n};\n"
    (ROOT/"Native/src/chinese_arcade_names.inc").write_text(header)


def ui_artwork():
    specs = text_specs()
    (REVIEW/"ui.json").write_text(json.dumps(specs, ensure_ascii=False, indent=2)+"\n")
    audit = []
    for path in sorted(SOURCE.rglob("*.idasmesh")):
        if path.stem not in specs:
            continue
        chunks = read_mesh(path)
        textures = read_textures(path.parent/"textures/textures.idastex")
        source_count = len(textures)
        entries = []
        for index, spec in specs[path.stem].items():
            if "hint" in spec:
                batches = chunks[int(index)]["batches"]
                if len(batches) != 2:
                    raise ValueError(f"Expected title and input hint: {path}:{index}")
                heading = max(range(2), key=lambda i:max(v[2] for v in batches[i]["vertices"])-
                              min(v[2] for v in batches[i]["vertices"]))
                layers = [dict(spec, batch=heading),
                          dict(text="", controls=spec["hint"], input_hint=True, batch=1-heading)]
            else:
                layers = spec.get("layers", [spec])
            entries.extend((int(index), layer) for layer in layers)
        for index, spec in entries:
            chunk = chunks[index]
            selected = [chunk["batches"][spec["batch"]]] if "batch" in spec else chunk["batches"]
            points = [v for b in selected for v in b["vertices"]]
            left, right = min(v[1] for v in points), max(v[1] for v in points)
            bottom, top = min(v[2] for v in points), max(v[2] for v in points)
            if "bounds" in spec:
                left, bottom, right, top = spec["bounds"]
            if spec.get("natural_width"):
                ink = start_condition_lettering(spec["text"])
                left, bottom, top = 0, -1.22, -.98
                right = .24*ink.width/ink.height
            width, height = min(2048, max(16, round(128*(right-left)/(top-bottom)))), 128
            if "texture_size" in spec:
                width, height = spec["texture_size"]
            extent, size = (left, bottom, right, top), (width, height)
            original = source_layer(selected, textures, extent, size)
            if "glow_of" in spec:
                label = source_layer(chunks[spec["glow_of"]]["batches"], textures, extent, size)
                alpha = label.getchannel("A")
                scale = height/((top-bottom)*100)
                if spec.get("spread"):
                    radius = max(1, round(scale*spec["spread"]))
                    alpha = alpha.filter(ImageFilter.MaxFilter(radius*2+1))
                alpha = alpha.filter(ImageFilter.GaussianBlur(max(1, scale*2)))
                image = Image.new("RGBA", size, spec.get("color", "#ffe7a0"))
                image.putalpha(alpha)
            elif spec.get("course_card"):
                image = course_card(spec, index, size)
            else:
                image = render_text(spec, original, extent, size)
            texture = len(textures)
            textures.append((width, height, image.tobytes()))
            base = copy.deepcopy(selected[0])
            words = list(base["words"])
            words[6], words[7] = 0x4a, 4
            words[2] = (4<<29)|(5<<26)|(1<<15)|(1<<16)|(1<<20)|(3<<6)
            base["words"] = tuple(words)
            material = list(base["material"])
            material[9] = texture
            base["material"] = tuple(material)
            vertices = [(0xe0000000,x,y,points[0][3],0.,0.,1.,u,v,0xffffffff,0)
                        for x,y,u,v in ((left,top,0,0),(left,bottom,0,1),
                                        (right,top,1,0),(right,bottom,1,1))]
            vertices = [struct.unpack("<I8f2I", struct.pack("<I8f2I", *v)) for v in vertices]
            base["vertices"], base["indices"] = vertices, (0,1,2,2,1,3)
            base["source_vertex_bytes"] = b"".join(
                struct.pack("<I5f2I", v[0], *v[1:4], v[7], v[8], v[9], v[10]) for v in vertices)
            if "batch" in spec:
                chunk["batches"][chunk["batches"].index(selected[0])] = base
            else:
                chunk["batches"] = [base]
        relative = path.relative_to(SOURCE)
        target = OUTPUT/"original_assets"/relative
        target.parent.mkdir(parents=True, exist_ok=True)
        write_binary(target, chunks)
        write_textures(target.parent/"textures/textures.idastex", textures)
        audit.append(dict(asset=relative.as_posix(), base_textures=source_count,
                          chunks=sorted(int(i) for i in specs[path.stem]),
                          texture_patches=[],
                          source_sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    previous = OUTPUT/"manifest.json"
    if previous.is_file():
        retained = {row["asset"] for row in audit}
        for row in json.loads(previous.read_text())["banks"]:
            if row["asset"] not in retained:
                stale = OUTPUT/"original_assets"/row["asset"]
                for file in (stale, stale.parent/"textures/textures.idastex"):
                    if file.is_file():
                        file.unlink()
    previous.write_text(json.dumps(dict(version=2, language="zh-Hans", banks=audit), indent=2)+"\n")
    return len(audit)


def build():
    from build_chinese_story import build as build_story
    build_story()
    name_tables()
    typography = {
        "source": "Text re-typeset for the arcade/iOS UI; no whole PSP UI images",
        "fonts": {role: dict(file=font_reference(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest())
                  for role, path in FONTS.items()},
    }
    (REVIEW/"typography.json").write_text(json.dumps(typography, indent=2)+"\n")
    print(f"{ui_artwork()} independently typeset UI banks; "
          f"{imported_course_artwork()} localized extension cards")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("artwork", "build"))
    args = parser.parse_args()
    if args.action == "artwork":
        print(f"{ui_artwork()} UI banks; {imported_course_artwork()} extension cards")
    else:
        build()
