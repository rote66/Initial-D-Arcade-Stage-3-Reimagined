import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("voice_transcripts", ROOT / "Tools/Transcribe-PspStoryVoices.py")
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def clip(sha="original-audio", bank=0, duration=3.0):
    return dict(sourceSha256=sha, duration=duration,
                aliases=[dict(bank=bank, id="B_example_start_000", events=[1])])


def raw(text, probability=0.9):
    return dict(transcription=[dict(text=text, tokens=[
        dict(text=text, p=probability), dict(text="[_EOT_]", p=0.01)])])


class VoiceTranscriptTests(unittest.TestCase):
    def test_candidate_never_becomes_reviewed_or_translated(self):
        result = tool.review_record(clip(), raw("\u96e8\u3067\u3082\u8d70\u308c\u308b\u306e\u304b"))
        self.assertEqual(result["status"], "unreviewed_asr")
        self.assertIsNone(result["chineseTranslation"])
        self.assertEqual(result["sourceSha256"], "original-audio")
        self.assertEqual(result["aliases"], clip()["aliases"])

    def test_special_tokens_do_not_reduce_text_score(self):
        result = tool.review_record(clip(), raw("\u884c\u304f\u305e"))
        self.assertAlmostEqual(result["tokenProbabilityGeometricMean"], 0.9)
        self.assertEqual(result["reviewFlags"], [])

    def test_empty_and_non_japanese_need_review(self):
        self.assertIn("empty", tool.review_record(clip(), raw(""))["reviewFlags"])
        self.assertIn("no_japanese_characters",
                      tool.review_record(clip(), raw("..."))["reviewFlags"])

    def test_low_probability_is_flagged_not_discarded(self):
        result = tool.review_record(clip(), raw("\u884c\u304f\u305e", 0.1))
        self.assertIn("low_token_probability", result["reviewFlags"])
        self.assertIn("very_low_probability_token", result["reviewFlags"])
        self.assertEqual(result["japaneseCandidate"], "\u884c\u304f\u305e")

    def test_invalid_probabilities_cannot_produce_nan_score(self):
        for probability in (float("nan"), float("inf"), -1, 2):
            result = tool.review_record(clip(), raw("\u884c\u304f\u305e", probability))
            self.assertIsNone(result["tokenProbabilityGeometricMean"])
            self.assertIn("low_token_probability", result["reviewFlags"])

    def test_unrealistic_length_is_flagged(self):
        result = tool.review_record(clip(duration=0.2), raw("\u3042" * 50))
        self.assertIn("implausible_text_length", result["reviewFlags"])

    def test_known_name_misspelling_is_not_silently_corrected(self):
        text = "\u9ad8\u6a4b\u51cc\u4ecb"
        result = tool.review_record(clip(), raw(text))
        self.assertIn("suspect_proper_name", result["reviewFlags"])
        self.assertEqual(result["japaneseCandidate"], text)

    def test_trial_visits_each_opponent_first(self):
        rows = [clip("a", 0), clip("b", 0), clip("c", 1), clip("d", 2), clip("e", 2)]
        self.assertEqual([c["sourceSha256"] for c in tool.trial_order(rows)],
                         ["a", "c", "d", "b", "e"])
        self.assertEqual(tool.trial_order([]), [])

    def test_trial_deduplicates_original_audio(self):
        rows = [clip("a", 0), clip("a", 1), clip("b", 1)]
        self.assertEqual([c["sourceSha256"] for c in tool.trial_order(rows)], ["a", "b"])

    def test_resume_rejects_incomplete_or_wrong_task_output(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "result.json"
            self.assertIsNone(tool.read_asr(target))
            target.write_text("{")
            self.assertIsNone(tool.read_asr(target))
            for value in ([], {}, {"transcription": []},
                          {"params": [], "transcription": []},
                          {"params": {"language": "en", "translate": False}, "transcription": []},
                          {"params": {"language": "ja", "translate": True}, "transcription": []}):
                target.write_text(json.dumps(value))
                self.assertIsNone(tool.read_asr(target))
            value = raw("\u884c\u304f\u305e")
            value["params"] = {"language": "ja", "translate": False}
            target.write_text(json.dumps(value))
            self.assertEqual(tool.read_asr(target), value)


if __name__ == "__main__":
    unittest.main()
