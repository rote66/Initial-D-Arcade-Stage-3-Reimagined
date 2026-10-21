"""Check text coverage and iOS hints without loading any PSP UI bitmap."""
import json
import re
import struct
import tempfile
import unicodedata
from pathlib import Path
import unittest
from unittest.mock import patch

import numpy as np
from fontTools.ttLib import TTFont
from PIL import Image
from chinese_ui_text import FONTS, GLYPH_ALIASES, IMPORTED_COURSES, font_reference, specs, rival_names
from make_chinese_arcade import (course_tile, course_title_artwork, course_title_lettering,
                                hint_lettering, lettering, race_title_lettering,
                                read_mesh, read_textures, render_text)
from build_chinese_story import independent_rows, read_dialogue, source_catalog
import build_chinese_story

ROOT = Path(__file__).resolve().parents[2]


class TypographyTests(unittest.TestCase):
    def test_font_metadata_has_no_host_paths(self):
        self.assertEqual(font_reference(FONTS["caption"]),
                         "Native/localization/zh-Hans/fonts/SourceHanSerifSC-Heavy.otf")
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(font_reference(Path(directory)/"brush.ttc"), "$IDAS3_BRUSH_FONT")
        directory = ROOT/"Native/localization/zh-Hans"
        typography = json.loads((directory/"typography.json").read_text())
        story = json.loads((directory/"story-build.json").read_text())
        for role, font in typography["fonts"].items():
            self.assertEqual(font["file"], font_reference(FONTS[role]))
            self.assertFalse(Path(font["file"]).is_absolute())
        for role, font in story["fonts"].items():
            self.assertEqual(font["file"], font_reference(FONTS[role]))
        self.assertEqual(story["name_label_font"]["file"], font_reference(FONTS["brush"]))

    def test_no_copied_ui_images(self):
        source = (ROOT/"Native/tools/make_chinese_arcade.py").read_text()
        self.assertNotIn("psp-artwork", source)
        data = json.dumps(specs(), ensure_ascii=False)
        self.assertNotIn("psp_bitmap", data)
        self.assertNotIn("psp_hint", data)
        self.assertNotIn("方向键", data)
        self.assertNotIn("Project D主页", data)

    def test_actual_input_hints(self):
        for bank, entries in specs().items():
            for item in entries.values():
                if not item.get("heading"):
                    continue
                controls = item["hint"]
                self.assertEqual(controls[0]["control"], "摇杆")
                self.assertIn(dict(control="START", action="确认"), controls)
                self.assertEqual(any("VIEW" in row["control"] for row in controls),
                                 bank == "v3sK02rival")
                if bank == "v3sK02rival":
                    self.assertIn(dict(control="长按VIEW", action="选曲"), controls)

    def test_d3_hint_frames_colors_and_size(self):
        controls = specs()["v3sS11mode"]["18"]["hint"]
        image = hint_lettering(controls)
        pixels = np.array(image)
        visible = pixels[pixels[:, :, 3] > 0]
        self.assertEqual({tuple(pixel[:3]) for pixel in visible},
                         {(255, 255, 255), (255, 187, 0)})
        self.assertEqual(image.height, 31*4)
        self.assertLessEqual(image.width, 247*4)
        # A control frame has a continuous horizontal stroke and a transparent interior.
        self.assertTrue((pixels[7*4, :70*4, 3] == 255).all())
        self.assertEqual(pixels[9*4, 2*4, 3], 0)
        self.assertTrue((pixels[:30*4, 137*4, 3] == 255).all())

    def test_original_at_mt_and_sticker(self):
        data = specs()
        self.assertEqual(data["v3sS06mission"]["0"]["text"], "AT")
        self.assertEqual(data["v3sS06mission"]["1"]["text"], "MT")
        self.assertEqual(data["v3sS06mission"]["0"]["subtitle"], "自动挡")
        self.assertEqual(data["v3sS06mission"]["1"]["subtitle"], "手动挡")
        self.assertNotIn("65", data["v3sS07tune"])
        for chunk in ("2", "3", "4", "5"):
            self.assertTrue(data["v3sS07tune"][chunk]["preserve_primary"])

    def test_style_roles_and_glyphs(self):
        fonts = {role: TTFont(path, fontNumber=0).getBestCmap() for role, path in FONTS.items()}
        for bank in specs().values():
            for entry in bank.values():
                for item in entry.get("layers", [entry]):
                    if "glow_of" in item:
                        continue
                    texts = [item.get("text", ""), item.get("subtitle", "")]
                    for row in item.get("hint", []):
                        for char in row["control"]+row["action"]:
                            self.assertIn(ord(char), fonts["body"])
                    for text in texts:
                        for char in text:
                            if char.isspace():
                                continue
                            self.assertIn(ord(char), fonts[item.get("role", "label")],
                                          f"Missing {char!r} in {item.get('role')}")
        for name in rival_names():
            for char in name:
                self.assertIn(ord(char), fonts["body"])
                self.assertIn(ord(char), fonts["brush"])
        self.assertEqual(rival_names()[17], "御木")

    def test_fixed_condition_widths(self):
        conditions = specs()["v3sK02rival"]
        self.assertEqual(conditions["4"]["bounds"], conditions["9"]["bounds"])
        self.assertEqual(conditions["7"]["bounds"], conditions["15"]["bounds"])
        self.assertEqual(conditions["7"]["text"], "晴")

    def test_title_spacing_and_mode_shape(self):
        mode = specs()["v3sS11mode"]
        self.assertGreater(mode["18"]["tracking"], .35)
        self.assertGreater(mode["18"]["ink_aspect"], 8)
        self.assertNotIn("pixel_height", mode["18"])
        self.assertEqual(mode["7"]["role"], "mode")
        self.assertGreater(mode["7"]["ink_aspect"], 7)
        self.assertEqual(mode["1"]["role"], "caption")
        self.assertEqual(mode["1"]["paragraph_align"], "left")

    def test_common_mode_description_anchor(self):
        mode = specs()["v3sS11mode"]
        original = Image.new("RGBA", (1024, 128), "white")
        starts = []
        for chunk in ("1", "2", "3"):
            item = mode[chunk]
            self.assertEqual(item["paragraph_left"], 112)
            image = render_text(item, original, (.64, -4.6, 5.76, -3.96), original.size)
            starts.append(image.getbbox()[0])
        self.assertLessEqual(max(starts)-min(starts), 1)

    def test_mode_descriptions_do_not_scale_with_their_text(self):
        mode = specs()["v3sS11mode"]
        original = Image.new("RGBA", (1024, 128), "white")
        extent = (.64, -4.6, 5.76, -3.96)
        expected = None
        for chunk in ("1", "2", "3"):
            item = dict(mode[chunk])
            self.assertEqual(item["font_size"], 12)
            self.assertEqual(item["line_pitch"], 14)
            self.assertEqual(item["paragraph_baseline"], 35)
            for text in ("模式\n模式", "模式内容公路最速传说模式内容\n模式"):
                item["text"] = text
                image = render_text(item, original, extent, original.size)
                # The first two glyphs must be identical regardless of other words/lines.
                probe = np.array(image.crop((224, 30, 276, 120)))
                if expected is None:
                    expected = probe
                np.testing.assert_array_equal(probe, expected)

    def test_happo_names_and_reference_wording(self):
        data = specs()
        self.assertEqual(data["v3sK02rival"]["95"]["text"], rival_names()[19])
        self.assertEqual(data["v3sK02rival"]["99"]["text"], "馆智幸")
        self.assertEqual(data["v3sK00rivalface"]["55"]["text"], rival_names()[19])
        self.assertEqual(data["v3sK00rivalface"]["59"]["text"], "馆智幸")
        self.assertEqual(rival_names()[16], "小柏海")
        self.assertEqual(data["v3sT02route"]["16"]["text"], "归路")
        self.assertEqual(data["v3sT02route"]["22"]["text"], "去路")

    def test_start_conditions_and_single_separators(self):
        data = specs()["start2d"]
        for chunk, name in zip((113, 109, 114, 107, 115, 111, 108),
                               ("上坡", "下坡", "去路", "归路", "顺时针", "逆时针", "逆行")):
            self.assertEqual(data[str(chunk)]["text"], "[ "+name)
            for day in (105, 112):
                text = data[str(chunk)]["text"]+data[str(day)]["text"]+data["106"]["text"]
                self.assertEqual(text.count("·"), 2)
                self.assertNotIn("··", text.replace(" ", ""))
        self.assertEqual(data["0"]["text"], "雪 ]")
        for chunk in (0, 105, 106, 107, 108, 109, 111, 112, 113, 114, 115, 118):
            self.assertTrue(data[str(chunk)]["fixed_baseline"])

    def test_dialogue_colon_is_not_a_fallback(self):
        data = (ROOT/"Native/data/localization/zh-Hans/font.bin").read_bytes()
        count = struct.unpack_from("<I", data, 20)[0]
        records = {struct.unpack_from("<I5f", data, 24+i*24)[0]:
                   struct.unpack_from("<I5f", data, 24+i*24)[1:] for i in range(count)}
        self.assertIn(ord("："), records)
        self.assertNotEqual(records[ord("：")], records[ord("?")])

    def test_every_story_and_dynamic_glyph_exists(self):
        data = (ROOT/"Native/data/localization/zh-Hans/font.bin").read_bytes()
        count = struct.unpack_from("<I", data, 20)[0]
        records = {struct.unpack_from("<I5f", data, 24+i*24)[0]:
                   struct.unpack_from("<I5f", data, 24+i*24)[1:] for i in range(count)}
        needed = set(map(chr, range(32, 127)))
        source = (ROOT/"Native/src/original_rival_dialog_tables.inc").read_text()
        for text in re.findall(r'"((?:\\x[0-9a-fA-F]{2})+)"', source):
            needed.update(unicodedata.normalize(
                "NFKC", bytes.fromhex(text.replace("\\x", "")).decode("euc_jp")))
        dialogue = (ROOT/"Native/data/localization/zh-Hans/dialog.bin").read_bytes()
        count = struct.unpack_from("<I", dialogue, 12)[0]
        at = 16
        for _ in range(count):
            playback = struct.unpack_from("<4I", dialogue, at)[3]
            at += 16
            for _ in range(playback):
                at += 4
                for field in range(2):
                    size = struct.unpack_from("<I", dialogue, at)[0]
                    at += 4
                    if field == 1:
                        needed.update(dialogue[at:at+size].decode())
                    at += size
        self.assertEqual(at, len(dialogue))
        for char in needed-{"\n"}:
            self.assertIn(ord(char), records, f"Missing story/dynamic glyph {char!r}")
            if char not in ("?", " "):
                self.assertNotEqual(records[ord(char)], records[ord("?")], repr(char))

    def test_completed_latin_caps_match_reference_height(self):
        data = (ROOT/"Native/data/localization/zh-Hans/font.bin").read_bytes()
        _, width, height, _, count = struct.unpack_from("<5I", data, 4)
        records = {struct.unpack_from("<I5f", data, 24+i*24)[0]:
                   struct.unpack_from("<I5f", data, 24+i*24)[1:] for i in range(count)}
        atlas = Image.frombytes("RGBA", (width, height), data[24+count*24:], "raw", "BGRA")
        def cap_bounds(char):
            x, y, w, h, _ = map(int, records[ord(char)])
            pixels = np.array(atlas.crop((x, y, x+w, y+h)))
            rows = np.nonzero((pixels[:, :, :3].max(2) > 127) & (pixels[:, :, 3] > 127))[0]
            return rows.min(), rows.max()-rows.min()+1
        top, height = cap_bounds("A")
        for char in "HJPYZ":
            y, h = cap_bounds(char)
            self.assertAlmostEqual(y, top, delta=1)
            self.assertAlmostEqual(h, height, delta=1)
        def glyph(char):
            x, y, w, h, _ = map(int, records[ord(char)])
            return np.array(atlas.crop((x, y, x+w, y+h)))
        np.testing.assert_array_equal(glyph("−"), glyph("-"))
        np.testing.assert_array_equal(glyph("’"), glyph("'"))
        self.assertEqual(records[ord("−")][4], records[ord("-")][4])
        self.assertEqual(records[ord("’")][4], records[ord("'")][4])
        x, y, w, h, _ = map(int, records[ord("P")])
        pixels = np.array(atlas.crop((x, y, x+w, y+h)))
        mask = (pixels[:, :, :3].max(2) > 127) & (pixels[:, :, 3] > 127)
        rows, columns = np.nonzero(mask)
        mask = mask[rows.min():rows.max()+1, columns.min():columns.max()+1]
        right = mask[:, mask.shape[1]//2:]
        self.assertGreater(right[:len(right)//2].sum(), right[len(right)//2:].sum(),
                           "Arcade P is vertically inverted instead of using its original v=1 top")

    def test_dynamic_latin_ink_and_proportional_spacing(self):
        data = (ROOT/"Native/data/localization/zh-Hans/font.bin").read_bytes()
        _, width, height, _, count = struct.unpack_from("<5I", data, 4)
        records = {struct.unpack_from("<I5f", data, 24+i*24)[0]:
                   struct.unpack_from("<I5f", data, 24+i*24)[1:] for i in range(count)}
        atlas = Image.frombytes("RGBA", (width, height), data[24+count*24:], "raw", "BGRA")
        for code in range(33, 127):
            x, y, w, h, advance = map(int, records[code])
            pixels = np.array(atlas.crop((x, y, x+w, y+h)))
            rows, columns = np.nonzero((pixels[:, :, :3].max(2) > 127) &
                                      (pixels[:, :, 3] > 127))
            self.assertTrue(len(columns), f"Empty glyph {chr(code)!r}")
            self.assertEqual(columns.min(), 0, chr(code))
            self.assertEqual(advance, columns.max()+2, chr(code))
        self.assertLess(records[ord("I")][4], records[ord("G")][4])
        for word in ("EIGHT−SIX", "LANCER EVO.III", "IMPREZA", "MR2", "RX-7"):
            for char in word:
                self.assertIn(ord(char), records)

    def test_imported_course_uniform_typography(self):
        approved = dict(ENNA="塩那", MOMIJI="枫树线", TSUBAKI="筑波",
                        GUNSAI="群赛", MYOGI_SPECIAL="真・妙义", USUI_SPECIAL="真・碓冰")
        self.assertEqual({folder:name for folder, name, _ in IMPORTED_COURSES if folder in approved},
                         approved)
        audit = json.loads((ROOT/"Native/localization/zh-Hans/imported-courses.json").read_text())
        self.assertEqual(len(audit["courses"]), 9)
        self.assertEqual({row["title_ink_height"] for row in audit["courses"]}, {41})
        self.assertEqual({row["title_style"] for row in audit["courses"]},
                         {"black-brush-white-halo"})
        self.assertEqual({row["tile_ink_height"] for row in audit["courses"]}, {22})
        self.assertEqual({tuple(row["tile_text_box"]) for row in audit["courses"]}, {(4.5, 35, 91.5, 57)})
        self.assertEqual({row["title_center_x"] for row in audit["courses"]}, {199})
        self.assertEqual({row["tile_center_x"] for row in audit["courses"]}, {48})
        for _, name, _ in IMPORTED_COURSES:
            cmap = TTFont(FONTS["brush"], fontNumber=0).getBestCmap()
            for char in name:
                rendered = GLYPH_ALIASES.get("brush", {}).get(char, char)
                self.assertTrue(ord(rendered) in cmap, f"Missing course glyph: {char!r}")

    def test_imported_titles_match_original_course_strokes(self):
        bank = ROOT/"Native/data/localization/zh-Hans/original_assets/menus/v3/v3sK00common"
        chunk = read_mesh(bank/"v3sK00common.idasmesh")[33]["batches"][0]
        width, height, rgba = read_textures(bank/"textures/textures.idastex")[chunk["material"][9]]
        original = np.frombuffer(rgba, np.uint8).reshape(height, width, 4)
        dark = (original[:, :, :3].max(2) < 64) & (original[:, :, 3] > 220)
        rows = np.nonzero(dark)[0]
        points = chunk["vertices"]
        native_height = (max(v[2] for v in points)-min(v[2] for v in points))*100
        reference_height = round((rows.max()-rows.min()+1)*native_height/height)
        for _, name, _ in IMPORTED_COURSES:
            title, _ = course_title_lettering(name)
            pixels = np.array(title)
            dark = (pixels[:, :, :3].max(2) < 64) & (pixels[:, :, 3] > 220)
            rows = np.nonzero(dark)[0]
            self.assertAlmostEqual(rows.max()-rows.min()+1, reference_height, delta=1)
            halo_rows = np.nonzero(pixels[:, :, 3] > 1)[0]
            self.assertGreaterEqual(rows.min()-halo_rows.min(), 5)
            white = (pixels[:, :, :3].min(2) > 240) & (pixels[:, :, 3] > 0)
            self.assertTrue(white.any())
            self.assertTrue((pixels[:, :, 3][white] < 200).any())

    def test_original_and_extension_title_alignment(self):
        original = specs()["v3sK00common"]
        names = [item["text"] for item in original.values() if item.get("course_title")
                 and not item.get("snow")]
        names.extend(name for _, name, _ in IMPORTED_COURSES)
        photo = Image.new("RGBA", (384, 256), "#555555")
        for name in names:
            pixels = np.array(course_title_artwork(name))
            dark = (pixels[:, :, :3].max(2) < 64) & (pixels[:, :, 3] > 220)
            y, x = np.nonzero(dark)
            self.assertAlmostEqual((x.min()+x.max()+1)/2, 199, delta=1)
            self.assertAlmostEqual(y.min(), 49, delta=1)
            self.assertAlmostEqual(y.max()-y.min()+1, 41, delta=1)
            tile = np.array(course_tile(photo, name, "高级"))
            white = (tile[35*4:, :, :3].min(2) > 240) & (tile[35*4:, :, 3] > 220)
            y, x = np.nonzero(white)
            self.assertAlmostEqual((x.min()+x.max()+1)/8, 48, delta=.5)

    def test_snow_name_and_black_badge(self):
        data = specs()
        for item in (data["v3sK00common"]["32"], data["v3sK01course"]["16"]):
            self.assertEqual(item["text"], "秋名")
            self.assertTrue(item["snow"])
        self.assertEqual(data["start2d"]["80"]["text"], "秋名")
        self.assertTrue(data["start2d"]["80"]["snow_intro"])
        image = course_title_artwork("秋名", snow=True)
        pixels = np.array(image)
        # The snow glyph is white inside the black badge, not appended to the name.
        badge = pixels[40:100, 200:280]
        self.assertTrue(((badge[:, :, :3].min(2) > 240) & (badge[:, :, 3] > 220)).any())
        self.assertTrue(((badge[:, :, :3].max(2) < 32) & (badge[:, :, 3] > 220)).any())
        tile = course_tile(Image.new("RGBA", (384, 256), "#555555"), "秋名", "超高级", snow=True)
        self.assertEqual(tile.getpixel((45*4, 49*4)), (0, 0, 0, 255))
        self.assertTrue((np.array(tile)[49*4:63*4, 57*4:83*4, :3].min(2) > 240).any())

    def test_independent_d3_race_title(self):
        title = np.array(race_title_lettering("秋名"))
        snow = np.array(race_title_lettering("雪", snow=True))
        for pixels in (title, snow):
            white = (pixels[:, :, :3].min(2) > 240) & (pixels[:, :, 3] > 220)
            rgb = pixels[:, :, :3].astype(int)
            blue = (rgb[:, :, 2] > rgb[:, :, 0]+40) & (pixels[:, :, 3] > 10)
            self.assertTrue(white.any())
            self.assertTrue(blue.any())
            self.assertLess(white.sum(), pixels.shape[0]*pixels.shape[1]*.2)
        self.assertTrue(((snow[:, :, :3].max(2) < 32) & (snow[:, :, 3] > 220)).any())

    def test_all_independent_stories_complete_and_reproducible(self):
        rows, report = independent_rows()
        actual = read_dialogue((ROOT/"Native/data/localization/zh-Hans/dialog.bin").read_bytes())
        self.assertEqual(report["records"], 1152)
        self.assertEqual(report["independent_records"], 1152)
        self.assertEqual(report["retained_legacy_records"], 0)
        self.assertEqual(report["review_entries"], 3965)
        self.assertEqual(report["unique_source_passages"], 3788)
        _, pools = source_catalog()
        cmaps = {role: TTFont(FONTS[role]).getBestCmap() for role in ("body", "caption")}
        for key, row in rows.items():
            self.assertEqual(actual[key], row)
            original = pools[key[0]][key[1]]["tokens"]
            referenced = [at for at, command, _ in row[1] if not command]
            self.assertEqual(referenced, [i for i, t in enumerate(original)
                                          if not t["bytes"].startswith("*")])
            for _, _, text in row[1]:
                self.assertFalse(any(0xe000 <= ord(char) <= 0xf8ff for char in text))
                self.assertNotIn("?", text)
                for char in text:
                    if char.isspace():
                        continue
                    role = "body" if ord(char) < 128 else "caption"
                    self.assertIn(ord(char), cmaps[role], f"Independent font cannot render {char!r}")
        source = (ROOT/"Native/tools/build_chinese_story.py").read_text()
        self.assertNotIn("decode_gim", source)
        self.assertNotIn("PatchFont", source)
        self.assertNotIn("psp-artwork", source)
        self.assertNotIn('read_dialogue((OUTPUT', source)
        self.assertNotIn('(OUTPUT / "font.bin").read_bytes()', source)

    def test_independent_font_has_no_extracted_private_slots(self):
        data = (ROOT/"Native/data/localization/zh-Hans/font.bin").read_bytes()
        count = struct.unpack_from("<I", data, 20)[0]
        codes = {struct.unpack_from("<I", data, 24+i*24)[0] for i in range(count)}
        private = {code for code in codes if 0xe000 <= code <= 0xf8ff}
        expected = {base+i for base in (0xf100, 0xf200) for i in range(len(rival_names()))}
        self.assertEqual(private, expected)

    def test_story_and_font_rebuild_without_existing_outputs(self):
        original = ROOT/"Native/data/localization/zh-Hans"
        with tempfile.TemporaryDirectory() as output:
            with patch.object(build_chinese_story, "OUTPUT", Path(output)):
                rows, _ = independent_rows()
                build_chinese_story.write_dialogue(rows)
                build_chinese_story.update_unicode_font(rows)
            for filename in ("dialog.bin", "font.bin"):
                self.assertEqual((Path(output)/filename).read_bytes(),
                                 (original/filename).read_bytes())

    def test_accent_is_red_outline_not_red_fill(self):
        normal = lettering("速", "mode")
        accented = lettering("速", "mode", accent="速")
        def white_count(image):
            pixels = np.array(image)
            return ((pixels[:, :, 3] > 200) & (pixels[:, :, :3].min(2) > 240)).sum()
        self.assertEqual(white_count(normal), white_count(accented))
        pixels = np.array(accented)
        self.assertTrue(((pixels[:, :, 3] > 200) & (pixels[:, :, 0] > 230) &
                         (pixels[:, :, 1] < 50) & (pixels[:, :, 2] < 50)).any())


if __name__ == "__main__":
    unittest.main()
