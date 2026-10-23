#!/usr/bin/env python3
"""Run reproducible source checks without loading Unity or starting a headset."""

import argparse
import ast
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--format", action="store_true", help="Apply formatters before checking")
    args = parser.parse_args()
    files = json.loads((ROOT / "config/public-files.json").read_text())
    python = [p for p in files if p.endswith(".py")]
    shell = [p for p in files if p.endswith(".sh")]
    native = [p for p in files if Path(p).suffix in {".c", ".cpp", ".h", ".m", ".mm", ".cs"}]
    for name in python:
        ast.parse((ROOT / name).read_text(), filename=name)
    for name in files:
        if name.endswith((".csproj", ".props")):
            ET.parse(ROOT / name)
        elif name.endswith(".sh"):
            subprocess.run(["bash", "-n", name], cwd=ROOT, check=True)

    def run(command):
        subprocess.run(command, cwd=ROOT, check=True)

    run(
        [
            "uv",
            "tool",
            "run",
            "ruff==0.17.2",
            "format",
            *([] if args.format else ["--check"]),
            *python,
        ]
    )
    run(["uv", "tool", "run", "ruff==0.17.2", "check", *python])
    run(["clang-format", *(["-i"] if args.format else ["--dry-run", "--Werror"]), *native])
    run(
        [
            "uv",
            "tool",
            "run",
            "--from",
            "shfmt-py==4.2.0",
            "shfmt",
            "-w" if args.format else "-d",
            "-i",
            "2",
            *shell,
        ]
    )
    run(
        [
            "uv",
            "tool",
            "run",
            "--from",
            "shellcheck-py==0.11.0.1",
            "shellcheck",
            "-S",
            "warning",
            *shell,
        ]
    )
    for directory in [
        "script",
        "platform/scene-integrity",
        "audit-vr/runner-supervisor",
        "shaders",
    ]:
        run([sys.executable, "-m", "unittest", "discover", "-s", directory, "-p", "test_*.py"])
    print("Source formatting, lint, syntax and regression checks passed.")


if __name__ == "__main__":
    main()
