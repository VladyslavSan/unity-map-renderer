#!/usr/bin/env python3
"""Generate the UAX #9 lookup tables and the conformance fixture from the public UCD files.

Usage: generate-bidi-tables.py <ucd-dir>
  <ucd-dir> holds DerivedBidiClass.txt, BidiBrackets.txt, BidiMirroring.txt, BidiCharacterTest.txt and BidiTest.txt,
  downloaded from https://www.unicode.org/Public/UCD/latest/ucd/ (DerivedBidiClass.txt is in extracted/).
Writes:
  Assets/Code/MapRenderer.Unity/Text/Bidi/UnicodeBidiData.g.cs   sorted class runs, brackets, mirrors
  Assets/Fixtures/bidi-character-test-subset.txt                 samples of BidiCharacterTest.txt and BidiTest.txt
The raw UCD files are not committed. The generated header records their version and SHA-256.
"""
import hashlib
import os
import re
import sys

CLASSES = ["L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON",
           "LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI"]
LONG = {"Left_To_Right": "L", "Right_To_Left": "R", "Arabic_Letter": "AL", "European_Terminator": "ET"}
EXPLICIT_FORMATTING = {"LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI"}
TARGET_FIXTURE_LINES = 4000
PERMUTATION_MARKER = "# Permutations of sequences containing paired brackets"
MAX_CODEPOINT = 0x10FFFF
BIDI_TEST_STRIDE = 33
ALWAYS_INCLUDED_BIDI_TEST_INPUT = "AN WS S AN"
# One non-bracket code point per class, for turning a BidiTest.txt class sequence into text.
REPRESENTATIVES = {"L": 0x61, "R": 0x5D0, "AL": 0x627, "EN": 0x31, "ES": 0x2B, "ET": 0x24, "AN": 0x661,
                   "CS": 0x2C, "NSM": 0x300, "BN": 0xAD, "B": 0x2029, "S": 0x9, "WS": 0x20, "ON": 0x21}
# BD16 stack overflow: the brackets pair once, then 64 nested openers overflow, which cancels all pairing.
OVERFLOW_CASE = ("0061 0020 05D0 0020 0028 05D1 0029 " + " ".join(["0028"] * 64) + " 0020 0063;2;0;"
                 + " ".join(["0", "0", "1", "1", "1", "1"] + ["0"] * 67) + ";"
                 + " ".join(["0", "1", "5", "4", "3", "2"] + [str(i) for i in range(6, 73)]))


def read(ucd, name):
    path = os.path.join(ucd, name)
    with open(path, "rb") as handle:
        raw = handle.read()
    text = raw.decode("utf-8")
    version = re.search(r"# \S+-(\d+\.\d+\.\d+)\.txt", text).group(1)
    return text.split("\n"), version, hashlib.sha256(raw).hexdigest()


def bidi_test_cases(lines, table):
    """Turn BidiTest.txt into fixture lines. The paragraph level is fixed by the direction, or by P2 and P3."""
    cases, levels, order = [], "", ""
    for line in lines:
        line = line.strip()
        if line.startswith("@Levels:"):
            levels = line[len("@Levels:"):].strip()
        elif line.startswith("@Reorder:"):
            order = line[len("@Reorder:"):].strip()
        elif line and not line.startswith(("#", "@")):
            text, bitset = [f.strip() for f in line.split(";")]
            classes = text.split()
            if any(c in EXPLICIT_FORMATTING or c not in REPRESENTATIVES for c in classes):
                continue
            hexes = " ".join(f"{REPRESENTATIVES[c]:04X}" for c in classes)
            first_strong = next((c for c in classes if c in ("L", "R", "AL")), "L")
            auto_level = "1" if first_strong in ("R", "AL") else "0"
            for bit, direction, level in ((1, "2", auto_level), (2, "0", "0"), (4, "1", "1")):
                if int(bitset, 16) & bit:
                    cases.append((text, f"{hexes};{direction};{level};{levels};{order}"))
    for text, line in cases:
        for c, cp in zip(text.split(), line.split(";")[0].split()):
            assert table[int(cp, 16)] == c, f"representative U+{cp} is not class {c}"
    return cases


def parse_range(field):
    low, _, high = field.strip().partition("..")
    return int(low, 16), int(high or low, 16)


def build_classes(lines):
    table = ["L"] * (MAX_CODEPOINT + 1)
    for line in lines:
        match = re.match(r"# @missing: (\S+); (\w+)", line)
        if match:
            low, high = parse_range(match.group(1))
            table[low:high + 1] = [LONG.get(match.group(2), match.group(2))] * (high - low + 1)
    for line in lines:
        data = line.split("#", 1)[0].strip()
        if data:
            span, name = [f.strip() for f in data.split(";")]
            low, high = parse_range(span)
            table[low:high + 1] = [name] * (high - low + 1)
    return table


def runs(table):
    starts, classes = [], []
    for cp, name in enumerate(table):
        if not classes or classes[-1] != name:
            starts.append(cp)
            classes.append(name)
    return starts, classes


def pairs(lines, kind):
    rows = []
    for line in lines:
        data = line.split("#", 1)[0].strip()
        if data:
            fields = [f.strip() for f in data.split(";")]
            rows.append((int(fields[0], 16), int(fields[1], 16), fields[2] if kind == "brackets" else ""))
    return sorted(rows)


def array(name, kind, values, per_line):
    body = []
    for i in range(0, len(values), per_line):
        body.append("            " + ", ".join(values[i:i + per_line]) + ",")
    return f"        internal static readonly {kind}[] {name} =\n        {{\n" + "\n".join(body) + "\n        };\n"


def write_tables(out, sources, starts, classes, brackets, mirrors):
    provenance = "\n".join(f"   {name} {version} sha256 {digest}" for name, version, digest in sources)
    text = f"""/* <auto-generated> by Tools/generate-bidi-tables.py from the public Unicode Character Database. Do not edit.
{provenance}
   Unicode License v3 (see THIRD-PARTY-NOTICES.txt). */

namespace MapRenderer.Unity.Text.Bidi
{{
    /// <summary>Generated UAX #9 property tables. The lookups over them live in <see cref="UnicodeBidiData"/>.</summary>
    internal static partial class UnicodeBidiData
    {{
{array("RunStarts", "int", [f"0x{s:X}" for s in starts], 10)}
{array("RunClasses", "byte", [str(CLASSES.index(c)) for c in classes], 40)}
{array("BracketCodepoints", "int", [f"0x{b[0]:X}" for b in brackets], 10)}
{array("BracketPaired", "int", [f"0x{b[1]:X}" for b in brackets], 10)}
{array("BracketIsOpening", "bool", ["true" if b[2] == "o" else "false" for b in brackets], 20)}
{array("MirrorCodepoints", "int", [f"0x{m[0]:X}" for m in mirrors], 10)}
{array("MirrorPartners", "int", [f"0x{m[1]:X}" for m in mirrors], 10)}    }}
}}
"""
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text.replace("\n\n    }\n}", "\n    }\n}"))


def write_fixture(out, test_lines, bidi_test_lines, table, version, digest, bidi_test_digest):
    """Keep every hand-written case, and a stride sample of the generated permutations after them."""
    hand_written, permutations = [], []
    bucket = hand_written
    for line in test_lines:
        if line.startswith(PERMUTATION_MARKER):
            bucket = permutations
        if not line or line.startswith("#"):
            continue
        cps = [int(c, 16) for c in line.split(";")[0].split()]
        if not any(table[c] in EXPLICIT_FORMATTING for c in cps):
            bucket.append(line)
    generic = bidi_test_cases(bidi_test_lines, table)
    sampled = [line for _, line in generic[::BIDI_TEST_STRIDE]]
    forced = [line for text, line in generic if text == ALWAYS_INCLUDED_BIDI_TEST_INPUT and line not in sampled]
    stride = max(1, len(permutations) // max(1, TARGET_FIXTURE_LINES - len(hand_written)))
    sample = hand_written + [OVERFLOW_CASE] + permutations[::stride] + sampled + forced
    header = (f"# Subset of BidiCharacterTest-{version}.txt (sha256 {digest}), Unicode License v3.\n"
              f"# Kept: no explicit formatting characters; all {len(hand_written)} hand-written lines, and one in {stride} "
              f"of the {len(permutations)} generated permutation lines; one BidiTest.txt case in {BIDI_TEST_STRIDE} "
              f"of {len(generic)} (BidiTest-{version}.txt sha256 {bidi_test_digest}; non-formatting, one representative code point per class), "
              f"plus every case of '{ALWAYS_INCLUDED_BIDI_TEST_INPUT}'; and one hand-written BD16 overflow case.\n"
              "# Fields: code points; paragraph direction (0 LTR, 1 RTL, 2 auto); paragraph level; "
              "resolved levels (x = removed); visual order.\n")
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(header + "\n".join(sample) + "\n")
    return len(sample)


def main(argv):
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    ucd = argv[0]
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
    class_lines, class_version, class_digest = read(ucd, "DerivedBidiClass.txt")
    bracket_lines, bracket_version, bracket_digest = read(ucd, "BidiBrackets.txt")
    mirror_lines, mirror_version, mirror_digest = read(ucd, "BidiMirroring.txt")
    test_lines, test_version, test_digest = read(ucd, "BidiCharacterTest.txt")
    bidi_test_lines, bidi_test_version, bidi_test_digest = read(ucd, "BidiTest.txt")
    versions = {class_version, bracket_version, mirror_version, test_version, bidi_test_version}
    assert len(versions) == 1, f"the UCD files come from different Unicode versions: {sorted(versions)}"
    table = build_classes(class_lines)
    starts, classes = runs(table)
    write_tables(os.path.join(root, "Assets/Code/MapRenderer.Unity/Text/Bidi/UnicodeBidiData.g.cs"),
                 [("DerivedBidiClass.txt", class_version, class_digest),
                  ("BidiBrackets.txt", bracket_version, bracket_digest),
                  ("BidiMirroring.txt", mirror_version, mirror_digest)],
                 starts, classes, pairs(bracket_lines, "brackets"), pairs(mirror_lines, "mirrors"))
    kept = write_fixture(os.path.join(root, "Assets/Fixtures/bidi-character-test-subset.txt"),
                         test_lines, bidi_test_lines, table, test_version, test_digest, bidi_test_digest)
    print(f"{len(starts)} class runs, {kept} fixture lines")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
