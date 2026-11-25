import tempfile
import unittest
from pathlib import Path

from scripts.build_mobile_han import build


class MobileHanTests(unittest.TestCase):
    def test_full_syllable_initial_and_fuzzy_groups(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp) / "characters.dict.yaml"
            base.write_text(
                "啊\ta\t1\n安\tan\t99\n伸\tshen\t5\n身\tshen\t4\n"
                "生\tsheng\t100\n伸手\tshen shou\t200\n👌\tshen\t300\n",
                encoding="utf-8",
            )
            initials = "abbrev/^([a-z]).+$/$1/"
            out = Path(tmp) / "chars.lua"
            build(base, {"full": [initials], "fuzzy": ["fuzz/eng$/en/", initials]}, out)
            full, fuzzy = out.read_text(encoding="utf-8").split("  fuzzy = {")
            self.assertIn('["a"] = "啊"', full)
            self.assertIn('["shen"] = "伸身"', full)
            self.assertIn('["s"] = "生伸身"', full)
            self.assertIn('["shen"] = "伸身生"', fuzzy)
            self.assertNotIn("伸手", full + fuzzy)
            self.assertNotIn("👌", full + fuzzy)
