#!/usr/bin/env python3
"""Check that a Git tree contains only the expected source and configuration files."""

from __future__ import annotations

import argparse
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
import os
import re
import subprocess
import sys


MAX_BYTES = 1024 * 1024
ROOT_FILES = {".gitignore", "LICENSE", "README.md"}
FIXTURE_FILES = {
    "fixture/Assets/Editor.meta",
    "fixture/Assets/Validation.meta",
    "fixture/Packages/manifest.json",
    "fixture/Packages/packages-lock.json",
    "fixture/ProjectSettings/ProjectVersion.txt",
}
REGULAR_FILE_MODES = {b"100644", b"100755"}
PATH_COMPONENT = re.compile(r"[A-Za-z0-9_.-]+")
NON_TEXT_BYTE = re.compile(rb"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


@dataclass(frozen=True)
class Entry:
    path: bytes
    mode: bytes
    oid: bytes
    stage: int = 0


def git(repo: Path, *args: str, input_bytes: bytes | None = None) -> bytes:
    return subprocess.run(
        ["git", "-C", str(repo), *args],
        input=input_bytes,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=True,
    ).stdout


def entries(repo: Path, staged: bool) -> Iterator[Entry]:
    if staged:
        records = git(repo, "ls-files", "--stage", "--full-name", "-z")
    else:
        records = git(repo, "ls-tree", "-r", "--full-tree", "-z", "HEAD")

    for record in records.split(b"\0"):
        if not record:
            continue
        fields, path = record.split(b"\t", 1)
        if staged:
            mode, oid, stage = fields.split()
            yield Entry(path, mode, oid, int(stage))
        else:
            mode, _, oid = fields.split()
            yield Entry(path, mode, oid)


def allowed_path(path_bytes: bytes) -> bool:
    path = path_bytes.decode("ascii", errors="replace")
    parts = path.split("/")
    if any(part in {".", ".."} or not PATH_COMPONENT.fullmatch(part) for part in parts):
        return False
    if path in ROOT_FILES or path in FIXTURE_FILES:
        return True

    suffix = PurePosixPath(path).suffix
    if len(parts) == 3 and parts[:2] == [".github", "workflows"]:
        return suffix in {".yml", ".yaml"}
    if len(parts) >= 2 and parts[0] == "docs":
        return suffix == ".md"
    if len(parts) >= 2 and parts[0] in {"tools", "tests"}:
        return suffix == ".py"
    if len(parts) >= 4 and parts[:2] == ["fixture", "Assets"] and parts[2] in {"Editor", "Validation"}:
        return suffix in {".cs", ".shader", ".meta"}
    return False


def is_utf8_text(blob: bytes) -> bool:
    if NON_TEXT_BYTE.search(blob):
        return False
    try:
        blob.decode("utf-8")
    except UnicodeDecodeError:
        return False
    return True


def audit(repo: Path, staged: bool) -> list[str]:
    issues = []
    candidates = []
    for entry in entries(repo, staged):
        # repr escapes unusual Git filenames before writing them to the terminal.
        label = repr(entry.path)
        if entry.stage:
            issues.append(f"{label} has an unresolved index entry")
        elif entry.mode not in REGULAR_FILE_MODES:
            issues.append(f"{label} is not a regular file")
        elif not allowed_path(entry.path):
            issues.append(f"{label} is outside the source allowlist")
        else:
            candidates.append(entry)

    if candidates:
        object_info = git(
            repo,
            "cat-file",
            "--batch-check=%(objecttype) %(objectsize)",
            input_bytes=b"".join(entry.oid + b"\n" for entry in candidates),
        )
        for entry, info in zip(candidates, object_info.splitlines()):
            label = repr(entry.path)
            object_type, size = info.split()
            if object_type != b"blob":
                issues.append(f"{label} does not reference a Git blob")
            elif int(size) > MAX_BYTES:
                issues.append(f"{label} exceeds the {MAX_BYTES} byte limit")
            elif not is_utf8_text(git(repo, "cat-file", "blob", entry.oid.decode("ascii"))):
                issues.append(f"{label} is not UTF-8 text")
    return issues


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Audit committed HEAD for source-only publication.",
        epilog="Review text provenance manually. Passing this check does not establish authorship.",
    )
    parser.add_argument("--staged", action="store_true", help="check the complete Git index instead")
    args = parser.parse_args()
    try:
        root = git(Path.cwd(), "rev-parse", "--show-toplevel").removesuffix(b"\n")
        repo = Path(os.fsdecode(root))
        issues = audit(repo, args.staged)
    except subprocess.CalledProcessError as error:
        print(error.stderr.decode("utf-8", errors="replace").strip(), file=sys.stderr)
        return 1

    if issues:
        print("\n".join(issues), file=sys.stderr)
        return 1
    print("Publication check passed. Review text provenance before publication.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
