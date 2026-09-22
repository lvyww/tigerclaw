"""Evaluator integrity tests: incomplete results must not look like accuracy."""
import json
from pathlib import Path
import tempfile
import unittest
import analyze
import fuse


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        self.cases = {"a": dict(id="a", split="test", text="甲", code="jia", covered=True)}
        self.row = dict(id="a", split="test", text="甲", code="jia", rank=2, ms=1,
                        candidates=[dict(text="家", score=2), dict(text="甲", score=1)])

    def test_incomplete_and_duplicate_rejected(self):
        with self.assertRaises(ValueError):
            analyze.validate(self.cases, [], "test")
        with self.assertRaises(ValueError):
            analyze.validate(self.cases, [self.row, self.row], "test")
        bad = dict(self.row, rank=1)
        with self.assertRaises(ValueError):
            analyze.validate(self.cases, [bad], "test")

    def test_fusion_endpoints_and_tie(self):
        decode = {"a": self.row}
        qwen = {"a": dict(scores=[-3, -2])}
        self.assertEqual(analyze.winners(decode, qwen, 0)["a"], "家")
        self.assertEqual(analyze.winners(decode, qwen, 1)["a"], "甲")
        self.assertEqual(analyze.winners(decode, qwen, .5)["a"], "家")

    def test_failed_qwen_not_accepted(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "qwen.jsonl"
            failed = dict(id="a", error="disconnected", scores=None, count=2)
            path.write_text(json.dumps(failed) + "\n")
            with self.assertRaises(ValueError):
                analyze.qwen_rows([path], {"a": self.row})
            success = dict(id="a", error=None, scores=[-3, -2], count=2, ms=1)
            with path.open("a") as f:
                f.write(json.dumps(success) + "\n")
            self.assertEqual(analyze.qwen_rows([path], {"a": self.row})["a"]["scores"], [-3, -2])

    def test_rescue_and_recall_are_distinct(self):
        report = analyze.evaluate(self.cases, {"a": self.row}, {"a": dict(scores=[-3, -2], ms=1)}, 1)
        self.assertEqual(report["fusedTop1"], 1)
        self.assertEqual(len(report["rescued"]), 1)
        self.assertEqual(len(report["outsideTop10"]), 0)

    def test_materialized_fusion_and_fallback(self):
        row = dict(candidates=[dict(text="家",score=2,segments=[]), dict(text="甲",score=1,segments=[])])
        self.assertEqual(fuse.ranked(row, [-3, -2], 1)[0]["text"], "甲")
        self.assertEqual(fuse.ranked(row, None, 1)[0]["text"], "家")
        with self.assertRaises(ValueError):
            fuse.ranked(row, [float("nan"), -2], .5)

    def test_character_error_counts(self):
        for reference, hypothesis, distance in [("甲乙", "甲乙", 0), ("甲乙", "甲丙", 1), ("甲乙", "甲", 1), ("甲", "甲乙", 1), ("𠀀甲", "甲", 1)]:
            self.assertEqual(analyze.edit_distance(reference, hypothesis), distance)


if __name__ == "__main__":
    unittest.main()
