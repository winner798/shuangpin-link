from pathlib import Path
import runpy
from tempfile import TemporaryDirectory
import unittest


ROOT = Path(__file__).resolve().parents[1]
build_index = runpy.run_path(str(ROOT / "scripts/build_continuations.py"))["build_continuations"]


class PublicContinuationsTest(unittest.TestCase):
    def test_word_boundaries_ranking_and_curated_preference(self):
        with TemporaryDirectory() as tmp:
            root = Path(tmp)
            lexicon, phrases, data = (root / name for name in ("base", "phrases", "data"))
            lexicon.write_text(
                "空气\tkong qi\t300000\n质量\tzhi liang\t40000\n污染\twu ran\t70000\n"
                "空气污染\tkong qi wu ran\t80000\n空气质量\tkong qi zhi liang\t90000\n"
                "空气质量\tkong qi zhi liang\t2500\n空气甲乙\tkong qi jia yi\t50000\n",
                encoding="utf-8",
            )
            phrases.write_text("", encoding="utf-8")
            self.assertEqual(build_index([lexicon], phrases, data), 1)
            self.assertIn('["空气"] = "质量 污染"', data.read_text("utf-8"))
            phrases.write_text("空气\t清新\t质量\n", encoding="utf-8")
            build_index([lexicon], phrases, data)
            self.assertIn('["空气"] = "清新 质量 污染"', data.read_text("utf-8"))

    def test_curated_rows_are_unique_and_well_formed(self):
        with TemporaryDirectory() as tmp:
            out = Path(tmp) / "data"
            self.assertGreater(build_index([], ROOT / "rime/public_continuations.tsv", out), 0)
            phrases = Path(tmp) / "duplicate"
            phrases.write_text("空气\t清新\n空气\t污染\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate:2"):
                build_index([], phrases, out)
