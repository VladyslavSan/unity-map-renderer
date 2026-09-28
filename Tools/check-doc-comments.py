#!/usr/bin/env python3
"""Diff check for the doc-comment limits in docs/conventions-short.md (UMR-154).

Usage: check-doc-comments.py [base [head]]
  base  default: git merge-base HEAD main
  head  default: the working tree plus untracked files

Exit 0 = clean, 1 = findings, 2 = the tool itself failed (git error, self-test failure).
The rules are stated in docs/conventions.md, "How a line is counted". stdlib only.
"""
import difflib
import os
import re
import subprocess
import sys
import traceback

LIMITS = {"summary": 5, "param": 2, "returns": 2, "inline": 2}
EXEMPT_LABELS = ("Non-local invariant:", "Non-obvious why:", "Limitation:")
BLOCK_TAG = re.compile(
    r"</?(?:summary|para|remarks|returns|value|item|list\b[^>]*|param\b[^>]*|typeparam\b[^>]*|exception\b[^>]*)/?>")
OPEN_TAG = re.compile(r"<(summary|returns|param)\b(?:\s+name=\"([^\"]*)\")?[^>]*>")
DENYLIST = re.compile(
    r"\b(?:handback|hand-back|review arm|orchestrator|scratchpad)\b|\b(?:the|this|my) (?:brief|conversation)\b",
    re.IGNORECASE)
CITATION_EXTENSIONS = ("cs", "py", "sh", "hlsl", "cginc", "shader", "json", "asmdef", "csproj", "yml", "yaml", "txt")
CITATION = re.compile(r"([A-Za-z0-9_.-]+\.(?:" + "|".join(CITATION_EXTENSIONS) + r")):(\d+)(?:-(\d+))?")
RELOCATE = ("RELOCATE the reasoning to the docs/*-design.md that owns this code and leave a <=2-line pointer, "
            "or open one sentence with Non-local invariant: / Non-obvious why: / Limitation:")


def git(*args):
    try:
        return subprocess.run(["git", *args], check=True, capture_output=True, text=True, encoding="utf-8",
                              errors="replace").stdout
    except (subprocess.CalledProcessError, OSError) as error:
        detail = getattr(error, "stderr", "") or str(error)
        print(f"check-doc-comments: git {' '.join(args)} failed: {detail.strip()}", file=sys.stderr)
        sys.exit(2)


def is_text_line(comment):
    """A comment line counts when something other than block tags is left after them."""
    return BLOCK_TAG.sub("", comment).strip() != ""


def units(text):
    """Return (kind, name, first_line, text_line_indices, text) for every limited comment unit."""
    lines = text.split("\n")
    found = []
    i = 0
    while i < len(lines):
        stripped = lines[i].strip()
        if stripped.startswith("///"):
            j = i
            while j < len(lines) and lines[j].strip().startswith("///"):
                j += 1
            found.extend(doc_units(lines, i, j))
            i = j
        elif stripped.startswith("//"):
            j = i
            while j < len(lines) and lines[j].strip().startswith("//") and not lines[j].strip().startswith("///"):
                j += 1
            body = [k for k in range(i, j) if is_text_line(lines[k].strip()[2:])]
            found.append(("inline", "", i, body, "\n".join(lines[k] for k in body)))
            i = j
        else:
            i += 1
    return found


def doc_units(lines, start, end):
    """Split one /// run into summary, param and returns units."""
    result = []
    k = start
    while k < end:
        match = OPEN_TAG.search(lines[k].strip()[3:])
        if not match:
            k += 1
            continue
        kind, name = match.group(1), match.group(2) or ""
        close = f"</{kind}>"
        last = k
        while last < end and close not in lines[last]:
            last += 1
        last = min(last, end - 1)
        body = [m for m in range(k, last + 1) if is_text_line(lines[m].strip()[3:])]
        result.append((kind, name, k, body, "\n".join(lines[m] for m in body)))
        k = last + 1
    return result


def violations(before_text, after_text):
    """Return (failures, notes) as lists of (line_number, message) for units the diff authors or grows."""
    before = units(before_text)
    after = units(after_text)
    before_lines = [line.strip() for line in before_text.split("\n")]
    after_lines = [line.strip() for line in after_text.split("\n")]
    mapped = {}
    for block in difflib.SequenceMatcher(None, before_lines, after_lines, autojunk=False).get_matching_blocks():
        for offset in range(block.size):
            mapped[block.b + offset] = block.a + offset
    base_unit_of_line = {}
    for index, unit in enumerate(before):
        for line in unit[3]:
            base_unit_of_line[line] = index

    failures, notes = [], []
    for kind, name, first, body, text in after:
        limit = LIMITS[kind]
        count = len(body)
        if count <= limit:
            continue
        previous = 0
        for line in body:
            index = base_unit_of_line.get(mapped.get(line, -1))
            if index is not None and before[index][0] == kind and before[index][1] == name:
                previous = len(before[index][3])
                break
        if count <= previous:
            continue
        label = f"<{kind}{' ' + name if name else ''}>" if kind != "inline" else "// run"
        if any(marker in text for marker in EXEMPT_LABELS):
            notes.append((first + 1, f"{label} {count}/{limit} text lines, exempt by its stated label"))
        else:
            failures.append((first + 1, f"{label} {count}/{limit} text lines - {RELOCATE}"))
    return failures, notes


def added_comment_lines(before_text, after_text):
    """Yield (line_number, comment_text) for comment lines the diff adds."""
    before_lines = [line.strip() for line in before_text.split("\n")]
    after_all = after_text.split("\n")
    matcher = difflib.SequenceMatcher(None, before_lines, [line.strip() for line in after_all], autojunk=False)
    for tag, _, _, start, end in matcher.get_opcodes():
        if tag in ("insert", "replace"):
            for k in range(start, end):
                if after_all[k].strip().startswith("//"):
                    yield k + 1, after_all[k]


def citation_problems(comment, file_lengths):
    """Return problems in one added comment line. file_lengths maps a basename to its longest line count."""
    problems = []
    if DENYLIST.search(comment):
        problems.append("cites a transient conversation, not a document a later reader can open")
    for match in CITATION.finditer(comment):
        length = file_lengths.get(match.group(1))
        cited = [int(g) for g in match.group(2, 3) if g]
        if length is None:
            problems.append(f"cites {match.group(0)}, but no file of that name exists")
        elif max(cited) > length:
            problems.append(f"cites {match.group(0)}, but the file has {length} lines")
    return problems


CITATION_SUFFIXES = tuple("." + extension for extension in CITATION_EXTENSIONS)


def tracked_file_lengths():
    """Map a basename to its longest line count, for the files a citation can name."""
    names = git("ls-files").split("\n") + git("ls-files", "--others", "--exclude-standard").split("\n")
    lengths = {}
    for path in names:
        if path and path.endswith(CITATION_SUFFIXES) and os.path.isfile(path):
            with open(path, encoding="utf-8", errors="replace") as handle:
                lengths[os.path.basename(path)] = max(lengths.get(os.path.basename(path), 0), handle.read().count("\n") + 1)
    return lengths


def self_test(lengths):
    def block(count, tag="summary", name=""):
        opening = "<" + tag + (' name="' + name + '"' if name else "") + ">"
        body = [f"/// word{n} text" for n in range(count)]
        return "\n".join([f"/// {opening}"] + body + [f"/// </{tag}>", "void M();"])

    def real(before, after):
        return violations(before, after)[0]

    checks = [
        ("7 physical / 5 text summary passes", not real("", "/// <summary>\n///\n" + "\n".join(f"/// word{n}" for n in range(5)) + "\n/// </summary>\nvoid M();")),
        ("6 text lines fail", bool(real("", block(6)))),
        ("2 -> 15 fails", bool(real(block(2), block(15)))),
        ("sweep in a 39-line block passes", not real(block(39), block(39).replace("word5 ", "term5 "))),
        ("8 -> 9 fails", bool(real(block(8), block(9)))),
        ("exempt over-limit passes", not real("", block(9).replace("word0 text", "Limitation: word0 text"))),
        ("3-line param fails", bool(real("", block(3, "param", "x")))),
        ("3-line // run fails", bool(real("", "// a\n// b\n// c\nint x;"))),
        ("trailing + 2 comment lines passes", not real("", "int x = 1; // a\n// b\n// c\nint y;")),
        ("cite Tools/run-tests.sh:1 passes", not citation_problems("// see Tools/run-tests.sh:1", lengths)),
        ("cite :999999 fails", bool(citation_problems("// see Tools/run-tests.sh:999999", lengths))),
        ("see the handback fails", bool(citation_problems("// see the handback", lengths))),
        ("a bare word 'session' passes", not citation_problems("// a session cache", lengths)),
        ("'orchestrators' passes, the word boundary holds", not citation_problems("// reorchestrators", lengths)),
        ("<list>/<item> tag lines do not count",
         not real("", "/// <summary>\n/// <list type=\"bullet\">\n/// <item>\n/// a\n/// b\n/// c\n/// d\n/// e\n"
                      "/// </item>\n/// </list>\n/// <para/>\n/// </summary>\nvoid M();")),
        ("6 text lines still fail beside <list> tags",
         bool(real("", "/// <summary>\n/// <list>\n/// a\n/// b\n/// c\n/// d\n/// e\n/// f\n/// </list>\n/// </summary>\nvoid M();"))),
        ("plain mv pairs a deleted and an untracked file by basename",
         pair_moves(["Assets/A/Old.cs"], ["Assets/B/Old.cs", "Assets/B/New.cs"]) == [("Assets/B/Old.cs", "Assets/A/Old.cs"), ("Assets/B/New.cs", None)]),
        ("two deleted files of one basename pair with neither", pair_moves(["A/X.cs", "B/X.cs"], ["C/X.cs"]) == [("C/X.cs", None)]),
        ("a generated .g.cs file is not checked", not is_checked("Assets/Code/Bidi.g.cs")),
        ("a ThirdParty file is not checked", not is_checked("Assets/Code/ThirdParty/x/A.cs")),
        ("a hand-written Assets .cs file is checked", is_checked("Assets/Code/A.cs")),
    ]
    bad = [name for name, ok in checks if not ok]
    if bad:
        print("check-doc-comments: self-test FAILED: " + "; ".join(bad), file=sys.stderr)
        sys.exit(2)


def is_checked(path):
    """True for a hand-written C# file under Assets/: not vendored, not generated."""
    return (path.startswith("Assets/") and path.endswith(".cs") and "/ThirdParty/" not in path
            and not path.endswith(".g.cs"))


def pair_moves(deleted, untracked):
    """Return (path, old_path_or_None) per untracked path. A plain `mv` shows as a deleted file plus an
    untracked one, so an untracked path pairs with the deleted path of the same basename when that
    basename is unambiguous on both sides."""
    result = []
    for path in untracked:
        name = os.path.basename(path)
        candidates = [d for d in deleted if os.path.basename(d) == name]
        same = [u for u in untracked if os.path.basename(u) == name]
        result.append((path, candidates[0] if len(candidates) == 1 and len(same) == 1 else None))
    return result


def changed_files(base, head):
    """Return (path, base_path_or_None) for each added, modified or renamed checked file. None means new."""
    diff = git("diff", "--name-status", "-M", base, *([head] if head else []))
    entries, deleted = [], []
    for row in diff.split("\n"):
        cols = row.split("\t")
        if len(cols) < 2:
            continue
        if cols[0].startswith("D"):
            deleted.append(cols[1])
            continue
        entries.append((cols[-1], None if cols[0].startswith("A") else cols[1]))
    if not head:
        untracked = [p for p in git("ls-files", "--others", "--exclude-standard").split("\n") if p]
        entries += pair_moves(deleted, untracked)
    return [e for e in entries if is_checked(e[0])]


def main(argv):
    os.chdir(git("rev-parse", "--show-toplevel").strip())
    lengths = tracked_file_lengths()
    self_test(lengths)
    base = argv[0] if argv else git("merge-base", "HEAD", "main").strip()
    head = argv[1] if len(argv) > 1 else None
    failed = 0
    for path, old_path in changed_files(base, head):
        before = "" if old_path is None else git("show", f"{base}:{old_path}")
        if head:
            after = git("show", f"{head}:{path}")
        else:
            with open(path, encoding="utf-8") as handle:
                after = handle.read()
        if before == after:
            continue
        failures, notes = violations(before, after)
        for line, message in notes:
            print(f"{path}:{line}: note: {message}")
        for line, message in failures:
            print(f"{path}:{line}: {message}")
        for line, comment in added_comment_lines(before, after):
            for problem in citation_problems(comment, lengths):
                failures.append((line, problem))
                print(f"{path}:{line}: {problem}")
        failed += len(failures)
    if failed:
        print(f"check-doc-comments: {failed} finding(s)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception:  # an uncaught exception would exit 1, which run-tests.sh reads as "findings"
        traceback.print_exc()
        sys.exit(2)
