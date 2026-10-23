#!/usr/bin/env python3
"""Build the native app entrypoint from the current source before packaging."""

from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
output = root / "build/launch/NewEngineH3VR"
output.parent.mkdir(parents=True, exist_ok=True)
subprocess.run(
    [
        "xcrun",
        "clang",
        "-arch",
        "arm64",
        "-O2",
        "-Wall",
        "-Werror",
        str(root / "platform/launch/NativeLauncher.c"),
        "-o",
        str(output),
    ],
    check=True,
)
if subprocess.check_output(["lipo", "-archs", str(output)], text=True).strip() != "arm64":
    raise SystemExit("Launcher is not ARM64-only")
print(output)
