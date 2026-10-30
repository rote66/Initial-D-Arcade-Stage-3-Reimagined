import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("subtitles", ROOT / "Tools/Build-StoryVoiceSubtitles.py")
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class StorySubtitleTests(unittest.TestCase):
    def test_complete_independent_bilingual_source(self):
        source = json.loads((tool.AUTHORING / "source.json").read_text())
        rows = tool.translations(source)
        self.assertEqual(len(rows), 1190)
        self.assertEqual(sum(len(c["aliases"]) for c in source["clips"]), 1331)
        self.assertTrue(source["asr"]["noSubtitlePrompt"])
        self.assertFalse(source["asr"]["uploadAudio"])

    def test_translations_reject_missing_duplicate_and_wrong_bank(self):
        source = dict(clips=[dict(id="voice", bank=0)])
        with tempfile.TemporaryDirectory() as name:
            folder = Path(name)
            with self.assertRaises(ValueError):
                tool.translations(source, folder)
            path = folder / "01.tsv"
            path.write_text("voice\t\u6765\u5427\tCome on!\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "wrong source bank"):
                tool.translations(source, folder)
            path.unlink()
            path = folder / "00.tsv"
            path.write_text("voice\t\u6765\u5427\tCome on!\n" * 2, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate"):
                tool.translations(source, folder)

    def test_documented_ambiguities_keep_reference_review_provenance(self):
        source = json.loads((tool.AUTHORING / "source.json").read_text())
        review = json.loads((tool.AUTHORING / "reference-review.json").read_text())
        clips = {c["id"]: c for c in source["clips"]}
        rows = tool.translations(source)
        self.assertEqual(review["sourceImageSha256"], source["sourceImageSha256"])
        self.assertFalse(review["pspCodeExecuted"])
        self.assertFalse(review["humanAudioVerified"])
        self.assertEqual(len(review["clips"]), 4)
        self.assertEqual(sum(c["changed"] for c in review["clips"]), 3)
        for clip in review["clips"]:
            self.assertEqual(clip["sourceSha256"], clips[clip["id"]]["sourceSha256"])
            self.assertEqual(rows[clip["id"]], dict(zh=clip["zh"], en=clip["en"]))
            self.assertTrue(clip["editorialInference"])

    def test_english_wrap_preserves_words(self):
        text = "Don't underestimate the Speed Stars. We won't give up!"
        wrapped = tool.wrap(text, {c: .5 for c in text}, width=10)
        self.assertEqual(wrapped.replace("\n", " "), text)
        self.assertTrue(all(len(line) <= 20 for line in wrapped.splitlines()))

    def test_long_text_rejected(self):
        with self.assertRaises(ValueError):
            tool.wrap("\u8dd1" * 220, {"\u8dd1": 1})

    def test_production_manifest_excludes_patch_subtitles(self):
        media = json.loads((ROOT / "Native/data/story_voices/voices.json").read_text())
        self.assertEqual(media["version"], 2)
        self.assertEqual(media["languages"], ["zh-Hans", "en"])
        self.assertNotIn("patchDataSha256", media)
        self.assertEqual(len(media["banks"]), 30)
        for bank in media["banks"]:
            for clip in bank["clips"]:
                self.assertGreater(clip["seconds"], 0)
                for field in ("glyphs", "englishGlyphs"):
                    self.assertTrue(clip[field])
                    self.assertTrue(all(-2 <= i < len(media["advances"]) for i in clip[field]))

    def test_production_catalog_matches_current_authoring(self):
        source = json.loads((tool.AUTHORING / "source.json").read_text())
        rows = tool.translations(source)
        media = json.loads((ROOT / "Native/data/story_voices/voices.json").read_text())
        characters = sorted(set("".join(t for r in rows.values() for t in r.values())) - {" "})
        expected = {
            (alias["bank"], alias["id"]): rows[clip["id"]]
            for clip in source["clips"] for alias in clip["aliases"]
        }
        for bank in media["banks"]:
            for clip in bank["clips"]:
                for language, field in (("zh", "glyphs"), ("en", "englishGlyphs")):
                    text = "".join(
                        "\n" if i == -1 else " " if i == -2 else characters[i]
                        for i in clip[field])
                    self.assertEqual("".join(text.split()),
                                     "".join(expected[(bank["id"], clip["id"])][language].split()))


if __name__ == "__main__":
    unittest.main()
