import tempfile
import unittest
from pathlib import Path

from scripts.prepare import ROOT, write_initials_index


class PhraseIndexTests(unittest.TestCase):
    def test_curated_short_forms_fill_the_page_after_existing_choices(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            base = directory / "base.dict.yaml"
            base.write_text(
                "红绿灯\thong lv deng\t35000\n"
                "回流电\thui liu dian\t18000\n"
                "火力点\thuo li dian\t32000\n",
                encoding="utf-8",
            )
            extra = directory / "phrases.dict.yaml"
            extra.write_text(
                "红绿灯\thong lv deng\t1400\n"
                "合流点\the liu dian\t1000\n"
                "红亮点\thong liang dian\t950\n",
                encoding="utf-8",
            )
            output = directory / "initials.lua"
            write_initials_index(base, output, extra)
            self.assertIn('["hld"] = "红绿灯 火力点 回流电 合流点"', output.read_text(encoding="utf-8"))

    def test_shipped_phrases_can_be_indexed_without_the_upstream_corpus(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder) / "base.dict.yaml"
            base.touch()
            output = Path(folder) / "initials.lua"
            write_initials_index(base, output, ROOT / "rime/link_phrases.dict.yaml")
            index = output.read_text(encoding="utf-8")
            self.assertIn('["dctl"] = "端侧推理"', index)
            self.assertIn('["hylj"] = "会议链接"', index)
