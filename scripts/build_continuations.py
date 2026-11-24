"""Compile a small, deterministic next-word index from the shipped Chinese lexicon."""

from collections import defaultdict
import json
import re


HAN_WORD = re.compile(r"[\u3400-\u9fff]{1,12}")
MAX_PREFIX = 6
MAX_CHOICES = 3


def build_continuations(dictionaries, phrases, destination):
    frequencies = {}
    for path in dictionaries:
        with path.open(encoding="utf-8") as rows:
            for line in rows:
                fields = line.rstrip("\r\n").split("\t")
                if len(fields) != 3 or not fields[2].isascii() or not fields[2].isdecimal():
                    continue
                word, _, weight = fields
                score = int(weight)
                if score >= 1000 and HAN_WORD.fullmatch(word):
                    frequencies[word] = max(score, frequencies.get(word, 0))

    # Both halves must be words in their own right. A character prefix of a
    # proper name is rarely a useful suggestion after the user confirms it.
    ranked = defaultdict(list)
    for word, score in frequencies.items():
        if score < 2000:
            continue
        for cut in range(2, min(MAX_PREFIX, len(word) - 2) + 1):
            prefix, suffix = word[:cut], word[cut:]
            if prefix in frequencies and suffix in frequencies:
                ranked[prefix].append((score, suffix))
    choices = {prefix: [word for _, word in sorted(items, key=lambda x: (-x[0], x[1]))[:MAX_CHOICES]]
               for prefix, items in ranked.items()}

    # Explicit conversational continuations also cover phrases that would not
    # normally be entered in a dictionary as one compound word.
    seen_prefixes = set()
    with phrases.open(encoding="utf-8") as rows:
        for number, line in enumerate(rows, 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            prefix, *words = line.split("\t")
            if (not 2 <= len(prefix) <= MAX_PREFIX or not 1 <= len(words) <= MAX_CHOICES
                    or not all(HAN_WORD.fullmatch(w) for w in (prefix, *words))
                    or len(set(words)) != len(words) or prefix in seen_prefixes):
                raise ValueError(f"Invalid public continuation at {phrases.name}:{number}")
            seen_prefixes.add(prefix)
            choices[prefix] = list(dict.fromkeys(words + choices.get(prefix, [])))[:MAX_CHOICES]

    with destination.open("w", encoding="utf-8", newline="\n") as out:
        out.write("return {\n")
        for prefix, words in sorted(choices.items()):
            # Validated Han-only strings. Store one compact value per prefix.
            out.write(f'  ["{prefix}"] = {json.dumps(" ".join(words), ensure_ascii=False)},\n')
        out.write("}\n")
    return len(choices)
