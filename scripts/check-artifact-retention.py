#!/usr/bin/env python3
"""Fail when a GitHub Actions upload-artifact step omits retention-days.

Dependency-free: this reads workflow YAML as text and does not import a YAML
package. It understands the step and ``on:`` shapes used by this repository
and by the fixtures in ``--self-test``.

Exit status is 0 when every ``actions/upload-artifact`` step sets
``retention-days`` to an integer from 1 through 90. A missing or invalid
value exits 1. Unreadable input or a missing workflow path exits 2.

``push``, ``pull_request``, ``pull_request_target`` and ``schedule`` are
printed as warnings and do not change the exit status. Those events stay
outside the manual-only policy. ``tests/VerificationWorkflow.Tests.ps1`` is
what fails the gate when a workflow is no longer ``workflow_dispatch``-only.
This script only warns, so a retention audit still finishes and the checker
itself is not an automatic CI job.

``retention-days: 0`` is invalid here. The upload-artifact action treats 0 as
"use the repository default" (90 days on this public repository), which is
the accumulation this check exists to prevent.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
AUTOMATIC_TRIGGERS = ("push", "pull_request", "pull_request_target", "schedule")
RETENTION_MIN = 1
RETENTION_MAX = 90

_USES_RE = re.compile(
    r"^[ ]*(?:-[ ]+)?uses:[ ]*['\"]?actions/upload-artifact@[A-Za-z0-9._-]+"
)
_KEY_RE = re.compile(
    r"^[ ]*(?:-[ ]+)?(?P<key>['\"]?[A-Za-z_][\w-]*['\"]?)\s*:"
)
_RETENTION_RE = re.compile(
    r"^['\"]?retention-days['\"]?\s*:\s*(?P<quote>['\"]?)(?P<value>\d+)(?P=quote)\s*$"
)
_INLINE_RETENTION_RE = re.compile(
    r"['\"]?retention-days['\"]?\s*:\s*(?P<quote>['\"]?)(?P<value>\d+)(?P=quote)"
)
_TRIGGER_RE = re.compile(r"^[A-Za-z_][\w-]*$")
_ON_RE = re.compile(r"""^['\"]?on['\"]?\s*:\s*(.*)$""")


def strip_comment(line: str) -> str:
    """Remove an unquoted YAML comment. Trailing-comment columns stay stable."""
    chars: list[str] = []
    quote: str | None = None
    for char in line:
        if quote:
            chars.append(char)
            if char == quote:
                quote = None
            continue
        if char in ("'", '"'):
            quote = char
            chars.append(char)
            continue
        if char == "#":
            break
        chars.append(char)
    return "".join(chars).rstrip()


def indent_of(line: str) -> int:
    return len(line) - len(line.lstrip(" "))


def is_ignorable(line: str) -> bool:
    stripped = line.strip()
    return stripped == "" or stripped.startswith("#")


def key_at(line: str) -> tuple[str, int] | None:
    content = strip_comment(line)
    match = _KEY_RE.match(content)
    if not match:
        return None
    key = match.group("key").strip("'\"")
    return key, match.start("key")


def find_upload_steps(lines: list[str]) -> list[dict[str, object]]:
    steps: list[dict[str, object]] = []
    for index, line in enumerate(lines):
        if is_ignorable(line) or not _USES_RE.match(strip_comment(line)):
            continue
        uses = key_at(line)
        if uses is None or uses[0] != "uses":
            continue
        uses_column = uses[1]
        list_index = index
        list_indent = indent_of(line)
        if not re.match(r"^[ ]*-[ ]+uses:", strip_comment(line)):
            owner = _step_owner(lines, index, uses_column)
            if owner is None:
                continue
            list_index, list_indent = owner
        end = len(lines)
        for later in range(index + 1, len(lines)):
            if is_ignorable(lines[later]):
                continue
            if indent_of(lines[later]) <= list_indent:
                end = later
                break
        steps.append(
            {
                "uses_line": index,
                "start": list_index,
                "end": end,
                "uses_column": uses_column,
                "step_name": _step_name(lines, list_index, index),
            }
        )
    return steps


def _step_owner(lines: list[str], uses_index: int, uses_column: int) -> tuple[int, int] | None:
    """Return the list item that owns a ``uses`` key, when the key is a step key.

    A ``uses:`` line nested inside ``run: |`` is deeper than the step keys, so
    the first shallower line is not a list item exactly two spaces out. That
    line is ignored.
    """
    for earlier in range(uses_index - 1, -1, -1):
        if is_ignorable(lines[earlier]):
            continue
        item = re.match(r"^(?P<indent>[ ]*)-[ ]+\S", lines[earlier])
        if item and len(item.group("indent")) == uses_column - 2:
            return earlier, len(item.group("indent"))
        if indent_of(lines[earlier]) < uses_column:
            return None
    return None


def _step_name(lines: list[str], start: int, uses_index: int) -> str:
    for index in range(start, uses_index + 1):
        content = strip_comment(lines[index]).strip()
        match = re.match(r"^[ ]*-[ ]+name:\s*(.+)$", content)
        if match:
            return match.group(1).strip().strip("'\"")
    return "upload-artifact"


def retention_values(lines: list[str], step: dict[str, object]) -> tuple[list[tuple[int, int]], str | None]:
    """Return ``(line, days)`` pairs found under this step's ``with`` block.

    The second item is an error fragment when ``retention-days`` is present
    but not a bare integer. ``None`` means the key was absent.
    """
    start = int(step["start"])
    end = int(step["end"])
    uses_column = int(step["uses_column"])
    found: list[tuple[int, int]] = []
    invalid: str | None = None
    for index in range(start, end):
        located = key_at(lines[index])
        if located is None or located[0] != "with" or located[1] != uses_column:
            continue
        content = strip_comment(lines[index]).strip()
        _, _, rest = content.partition(":")
        rest = rest.strip()
        if rest.startswith("{"):
            inline_values = list(_INLINE_RETENTION_RE.finditer(rest))
            if re.search(r"['\"]?retention-days['\"]?\s*:", rest) and not inline_values:
                invalid = "not an integer from {0} through {1}".format(RETENTION_MIN, RETENTION_MAX)
            for match in inline_values:
                found.append((index, int(match.group("value"))))
            continue
        if rest:
            invalid = "must be a block or a one-line mapping"
            continue
        for later in range(index + 1, end):
            if is_ignorable(lines[later]):
                continue
            nested = key_at(lines[later])
            if nested is not None and nested[1] <= uses_column:
                break
            if indent_of(lines[later]) <= uses_column:
                break
            body = strip_comment(lines[later]).strip()
            if not re.match(r"^['\"]?retention-days['\"]?\s*:", body):
                continue
            match = _RETENTION_RE.match(body)
            if not match:
                invalid = "not an integer from {0} through {1}".format(RETENTION_MIN, RETENTION_MAX)
                continue
            found.append((later, int(match.group("value"))))
    return found, invalid


def retention_errors(display_name: str, lines: list[str], step: dict[str, object]) -> list[str]:
    step_name = str(step["step_name"])
    where = "{0}:{1}".format(display_name, int(step["uses_line"]) + 1)
    values, invalid = retention_values(lines, step)
    label = "upload-artifact step '{0}'".format(step_name)
    if invalid is not None:
        return ["ERROR: {0}: {1} retention-days is {2}.".format(where, label, invalid)]
    if not values:
        return [
            "ERROR: {0}: {1} has no retention-days. Set an integer from {2} through {3}.".format(
                where, label, RETENTION_MIN, RETENTION_MAX
            )
        ]
    if len(values) > 1:
        return ["ERROR: {0}: {1} sets retention-days more than once.".format(where, label)]
    line_index, days = values[0]
    location = "{0}:{1}".format(display_name, line_index + 1)
    if days < RETENTION_MIN or days > RETENTION_MAX:
        reason = "set an integer from {0} through {1}".format(RETENTION_MIN, RETENTION_MAX)
        if days == 0:
            reason = (
                "0 uses the repository default retention; set an integer from {0} through {1}"
            ).format(RETENTION_MIN, RETENTION_MAX)
        return ["ERROR: {0}: {1} retention-days is {2}; {3}.".format(location, label, days, reason)]
    return []


def automatic_trigger_warnings(display_name: str, lines: list[str]) -> list[str]:
    warnings: list[str] = []
    seen: set[str] = set()
    for index, line in enumerate(lines):
        if line.startswith((" ", "\t")) or line.lstrip().startswith("#"):
            continue
        match = _ON_RE.match(strip_comment(line).strip())
        if not match:
            continue
        rest = match.group(1).strip()
        triggers: list[tuple[int, str]] = []
        if rest:
            for name in re.findall(r"[A-Za-z_][\w-]*", rest):
                triggers.append((index, name))
        else:
            triggers.extend(_block_triggers(lines, index))
        for line_index, name in triggers:
            if name not in AUTOMATIC_TRIGGERS or name in seen:
                continue
            seen.add(name)
            warnings.append(
                "WARNING: {0}:{1}: declares automatic trigger '{2}'. "
                "Manual-only policy keeps workflow_dispatch; this warning does not fail the retention check.".format(
                    display_name, line_index + 1, name
                )
            )
    return warnings


def _block_triggers(lines: list[str], on_index: int) -> list[tuple[int, str]]:
    found: list[tuple[int, str]] = []
    child_indent: int | None = None
    for index in range(on_index + 1, len(lines)):
        if is_ignorable(lines[index]):
            continue
        current = indent_of(lines[index])
        if current == 0:
            break
        if child_indent is None:
            child_indent = current
        if current != child_indent:
            continue
        body = strip_comment(lines[index]).strip()
        if body.startswith("- "):
            body = body[2:].strip()
        name = body.split(":", 1)[0].strip().strip("'\"")
        if _TRIGGER_RE.fullmatch(name):
            found.append((index, name))
    return found


def analyze(display_name: str, text: str) -> tuple[list[str], list[str], int]:
    normalized = text.lstrip("\ufeff").replace("\r\n", "\n").replace("\r", "\n")
    lines = normalized.split("\n")
    steps = find_upload_steps(lines)
    errors: list[str] = []
    for step in steps:
        errors.extend(retention_errors(display_name, lines, step))
    warnings = automatic_trigger_warnings(display_name, lines)
    return errors, warnings, len(steps)


def display_path(path: Path) -> str:
    resolved = path.resolve()
    try:
        return resolved.relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return path.as_posix()


def collect_workflows(paths: list[Path]) -> list[Path]:
    found: list[Path] = []
    for path in paths:
        if path.is_dir():
            found.extend(sorted(path.glob("*.yml")))
            found.extend(sorted(path.glob("*.yaml")))
            continue
        if not path.is_file():
            raise FileNotFoundError(path)
        found.append(path)
    return found


def run(paths: list[Path]) -> int:
    workflows = collect_workflows(paths)
    if not workflows:
        print("ERROR: no workflow files found.", file=sys.stderr)
        return 2
    errors: list[str] = []
    warnings: list[str] = []
    steps = 0
    for path in workflows:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as exc:
            errors.append("ERROR: {0}: {1}".format(path, exc))
            continue
        file_errors, file_warnings, count = analyze(display_path(path), text)
        errors.extend(file_errors)
        warnings.extend(file_warnings)
        steps += count
    for message in warnings + errors:
        print(message, file=sys.stderr)
    if errors:
        return 1
    print(
        "OK: {0} upload-artifact steps across {1} workflows declare retention-days.".format(
            steps, len(workflows)
        )
    )
    return 0


def self_test() -> int:
    cases = _self_test_cases()
    failed = 0
    for case in cases:
        errors, warnings, _count = analyze(case["name"], case["text"])
        problems: list[str] = []
        if case.get("errors"):
            for needle in case["errors"]:
                if not any(needle in error for error in errors):
                    problems.append("missing error containing {0!r}".format(needle))
        elif errors:
            problems.append("unexpected errors: {0}".format(errors))
        for needle in case.get("warnings", []):
            if not any(needle in warning for warning in warnings):
                problems.append("missing warning containing {0!r}".format(needle))
        for needle in case.get("no_warnings", []):
            if any(needle in warning for warning in warnings):
                problems.append("unexpected warning containing {0!r}: {1}".format(needle, warnings))
        if case.get("warning_count") is not None and len(warnings) != case["warning_count"]:
            problems.append("warning count {0}, expected {1}: {2}".format(len(warnings), case["warning_count"], warnings))
        if problems:
            failed += 1
            print("SELF-TEST FAIL {0}: {1}".format(case["name"], "; ".join(problems)), file=sys.stderr)
    if failed:
        print("SELF-TEST FAILED: {0} of {1} cases".format(failed, len(cases)), file=sys.stderr)
        return 1
    print("SELF-TEST OK: {0} cases".format(len(cases)))
    return 0


def _workflow(body: str) -> str:
    return "name: Fixture\n" + body.strip("\n") + "\n"


def _self_test_cases() -> list[dict[str, object]]:
    valid = _workflow(
        """
on:
  workflow_dispatch:

jobs:
  build:
    steps:
      - name: Upload package
        uses: actions/upload-artifact@v6
        with:
          name: CycleArc-win-x64
          path: dist/*
          retention-days: 90
"""
    )
    return [
        {"name": "valid block", "text": valid, "warning_count": 0},
        {
            "name": "missing retention",
            "text": valid.replace("          retention-days: 90\n", ""),
            "errors": ["Upload package", "has no retention-days"],
            "warning_count": 0,
        },
        {
            "name": "comment does not count",
            "text": valid.replace("retention-days: 90", "# retention-days: 90"),
            "errors": ["has no retention-days"],
        },
        {
            "name": "neighbor step does not satisfy this step",
            "text": _workflow(
                """
on:
  workflow_dispatch:
jobs:
  build:
    steps:
      - name: Upload first
        uses: actions/upload-artifact@v6
        with:
          name: first
          retention-days: 14
      - name: Upload second
        uses: actions/upload-artifact@v6
        with:
          name: second
          path: out/*
"""
            ),
            "errors": ["Upload second", "has no retention-days"],
        },
        {
            "name": "zero means repository default",
            "text": valid.replace("retention-days: 90", "retention-days: 0"),
            "errors": ["retention-days is 0", "repository default"],
        },
        {
            "name": "above public maximum",
            "text": valid.replace("retention-days: 90", "retention-days: 91"),
            "errors": ["retention-days is 91"],
        },
        {
            "name": "non-integer retention",
            "text": valid.replace("retention-days: 90", "retention-days: later"),
            "errors": ["not an integer"],
        },
        {"name": "minimum literal", "text": valid.replace("retention-days: 90", "retention-days: 1"), "warning_count": 0},
        {
            "name": "quoted literal",
            "text": valid.replace("retention-days: 90", 'retention-days: "14"'),
            "warning_count": 0,
        },
        {
            "name": "inline with mapping",
            "text": _workflow(
                """
on:
  workflow_dispatch:
jobs:
  build:
    steps:
      - uses: actions/upload-artifact@v4
        with: {name: inline, path: out/*, retention-days: 14}
"""
            ),
            "warning_count": 0,
        },
        {
            "name": "retention outside with",
            "text": _workflow(
                """
on:
  workflow_dispatch:
jobs:
  build:
    steps:
      - name: Upload outside
        uses: actions/upload-artifact@v6
        retention-days: 14
        with:
          name: outside
          path: out/*
"""
            ),
            "errors": ["has no retention-days"],
        },
        {
            "name": "run script mention is not an upload",
            "text": _workflow(
                """
on:
  workflow_dispatch:
jobs:
  build:
    steps:
      - name: Show an example
        run: |
          uses: actions/upload-artifact@v6
          retention-days: 14
"""
            ),
            "warning_count": 0,
        },
        {
            "name": "crlf",
            "text": valid.replace("\n", "\r\n"),
            "warning_count": 0,
        },
        {
            "name": "duplicate retention",
            "text": valid.replace(
                "retention-days: 90\n",
                "retention-days: 90\n          retention-days: 14\n",
            ),
            "errors": ["more than once"],
        },
        {
            "name": "push warns only",
            "text": valid.replace("  workflow_dispatch:", "  workflow_dispatch:\n  push:"),
            "warnings": ["automatic trigger 'push'"],
            "warning_count": 1,
        },
        {
            "name": "pull_request warns only",
            "text": valid.replace("  workflow_dispatch:", "  pull_request:"),
            "warnings": ["automatic trigger 'pull_request'"],
            "warning_count": 1,
        },
        {
            "name": "schedule warns only",
            "text": valid.replace(
                "  workflow_dispatch:",
                "  schedule:\n    - cron: '0 0 * * *'",
            ),
            "warnings": ["automatic trigger 'schedule'"],
            "warning_count": 1,
        },
        {
            "name": "pull_request_target warns only",
            "text": valid.replace("  workflow_dispatch:", "  pull_request_target:"),
            "warnings": ["automatic trigger 'pull_request_target'"],
            "warning_count": 1,
        },
        {
            "name": "inline trigger list",
            "text": valid.replace("on:\n  workflow_dispatch:", "on: [push, workflow_dispatch]"),
            "warnings": ["automatic trigger 'push'"],
            "no_warnings": ["automatic trigger 'workflow_dispatch'"],
            "warning_count": 1,
        },
        {
            "name": "scalar push",
            "text": valid.replace("on:\n  workflow_dispatch:", "on: push"),
            "warnings": ["automatic trigger 'push'"],
            "warning_count": 1,
        },
        {
            "name": "comment and description do not warn",
            "text": _workflow(
                """
# historical push trigger removed
on:
  workflow_dispatch:
    inputs:
      note:
        description: push the button and open a pull_request
jobs:
  build:
    steps:
      - name: Upload note
        uses: actions/upload-artifact@v6
        with:
          name: note
          path: note.txt
          retention-days: 7
"""
            ),
            "warning_count": 0,
        },
        {
            "name": "download artifact is ignored",
            "text": _workflow(
                """
on:
  workflow_dispatch:
jobs:
  build:
    steps:
      - name: Download
        uses: actions/download-artifact@v6
        with:
          name: CycleArc-win-x64
"""
            ),
            "warning_count": 0,
        },
    ]


def main(argv: list[str]) -> int:
    if any(arg in ("-h", "--help") for arg in argv):
        print(__doc__.strip())
        return 0
    if "--self-test" in argv:
        if argv != ["--self-test"]:
            print("ERROR: --self-test does not take other arguments.", file=sys.stderr)
            return 2
        return self_test()
    paths = [Path(arg) for arg in argv] if argv else [REPO_ROOT / ".github" / "workflows"]
    try:
        return run(paths)
    except FileNotFoundError as exc:
        print("ERROR: {0}".format(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
