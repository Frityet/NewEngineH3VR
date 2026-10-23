#!/usr/bin/env python3
"""Validate the Git index against the reviewed source-only publication manifest."""

import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "config/public-files.json"
ALLOWED = {
    ".py",
    ".cs",
    ".csproj",
    ".sh",
    ".c",
    ".cpp",
    ".h",
    ".m",
    ".mm",
    ".patch",
    ".md",
    ".json",
    ".toml",
    ".props",
    ".yml",
    ".lock",
}
SPECIAL = {"LICENSE", ".gitignore", ".editorconfig", ".clang-format", ".gitattributes"}
FORBIDDEN_PARTS = {
    "build",
    "dist",
    "Library",
    "Temp",
    "obj",
    "bin",
    ".tools",
    "recovered",
    "recovered-full",
    "unity",
    "unity-full",
    "postprocessing-2017",
    "postprocessing-legacy",
}


def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args])


def main():
    errors = []
    try:
        expected = set(json.loads(git("show", ":" + MANIFEST)))
    except (subprocess.CalledProcessError, ValueError):
        print(
            "Stage config/public-files.json before running the publication check.", file=sys.stderr
        )
        return 1
    entries = git("ls-files", "--stage", "-z").decode().split("\0")
    actual = set()
    total = 0
    for entry in filter(None, entries):
        metadata, name = entry.split("\t", 1)
        mode, oid, stage = metadata.split()
        actual.add(name)
        p = Path(name)
        if mode not in {"100644", "100755"} or stage != "0":
            errors.append(f"{name}: non-regular file or unresolved merge")
            continue
        if name not in expected or FORBIDDEN_PARTS.intersection(p.parts):
            errors.append(f"{name}: outside reviewed source manifest")
        if p.suffix not in ALLOWED and name not in SPECIAL:
            errors.append(f"{name}: prohibited file type")
        data = git("cat-file", "blob", oid)
        total += len(data)
        try:
            text = data.decode("utf-8-sig")
        except UnicodeDecodeError:
            errors.append(f"{name}: binary content")
            continue
        if b"\0" in data or len(data) > 1024 * 1024:
            errors.append(f"{name}: binary or unexpectedly large source")
        if any(
            marker in text
            for marker in [
                "-----BEGIN " + "PRIVATE KEY-----",
                "-----BEGIN RSA " + "PRIVATE KEY-----",
            ]
        ):
            errors.append(f"{name}: private key material")
    for name in sorted(expected - actual):
        errors.append(f"{name}: reviewed file missing from index")
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(
        f"Publication check passed: {len(actual)} regular UTF-8 source/config/docs files, {total:,} bytes; no asset/binary file types."
    )
    print(
        "Manifest additions require provenance review; this check does not establish authorship of arbitrary source code."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
