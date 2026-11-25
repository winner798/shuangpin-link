"""Build a finite single-syllable index using the spelling rules shipped in each scheme."""
import json
import re
from collections import defaultdict


def compile_rule(line):
    fields = line.removesuffix("/").split("/")
    operation, pattern = fields[:2]
    replacement = fields[2] if len(fields) == 3 else ""
    if operation == "xlit":
        return operation, str.maketrans(pattern, replacement), ""
    if operation not in {"erase", "abbrev", "derive", "fuzz", "xform"}:
        raise ValueError(f"Unsupported mobile spelling rule {line}")
    return operation, re.compile(pattern), re.sub(r"\$(\d+)", r"\\g<\1>", replacement)


def spellings(reading, rules):
    # 0 is a full spelling, 1 a fuzzy spelling, and 2 a single initial.
    forms = {reading: 0}
    for operation, pattern, replacement in rules:
        projected = {}
        for form, priority in forms.items():
            if operation == "xlit":
                changed = form.translate(pattern)
            else:
                changed = pattern.sub(replacement, form)
            if operation in {"derive", "fuzz", "abbrev"}:
                projected[form] = min(projected.get(form, 2), priority)
                priority = max(priority, {"derive": 0, "fuzz": 1, "abbrev": 2}[operation])
            if changed:
                projected[changed] = min(projected.get(changed, 2), priority)
        forms = projected
    return forms


def build(char_table, profiles, destination):
    readings = defaultdict(dict)
    for line in char_table.read_text(encoding="utf-8").splitlines():
        fields = line.split("\t")
        if len(fields) != 3 or len(fields[0]) != 1 or not fields[2].isdigit():
            continue
        char, reading, weight = fields
        if re.fullmatch("[a-z]+", reading) and (
            0x3400 <= ord(char) <= 0x9fff or 0xf900 <= ord(char) <= 0xfaff or 0x20000 <= ord(char) <= 0x323af
        ):
            readings[reading][char] = int(weight)
    lines = ["return {"]
    for profile, formulas in profiles.items():
        rules = [compile_rule(formula) for formula in formulas]
        groups = defaultdict(lambda: defaultdict(dict))
        for reading, characters in readings.items():
            for code, tier in spellings(reading, rules).items():
                ranked = groups[code][tier]
                for char, weight in characters.items():
                    ranked[char] = max(weight, ranked.get(char, 0))
        lines.append(f"  {profile} = {{")
        for code, tiers in sorted(groups.items()):
            # A complete vowel such as 'a' must not pull in the abbreviated readings of 'an'.
            ordered = []
            for tier in ((0, 1) if 0 in tiers or 1 in tiers else (2,)):
                ranked = tiers.get(tier, {})
                ordered.extend(sorted(ranked, key=lambda ch: -ranked[ch]))
            chars = "".join(dict.fromkeys(ordered))
            lines.append(f"    [{json.dumps(code)}] = \"{chars}\",")
        lines.append("  },")
    destination.write_text("\n".join([*lines, "}"]) + "\n", encoding="utf-8")
