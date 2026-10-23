#!/usr/bin/env python3
"""Compose independently validated platform changes without losing earlier patches."""

from pathlib import Path
import argparse
import os
import hashlib
import json
import shutil
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument(
    "--unity-contents",
    default=os.environ.get("UNITY_CONTENTS"),
    help="Target Unity.app/Contents (also UNITY_CONTENTS); overrides editor auto-selection",
)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
local_unity = (
    root / ".tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents"
)
if args.unity_contents:
    unity = Path(args.unity_contents).resolve()
elif os.environ.get("UNITY_EDITOR"):
    unity = Path(os.environ["UNITY_EDITOR"]).resolve().parents[1]
elif (local_unity / "MacOS/Unity").is_file():
    unity = local_unity
else:
    unity = Path("/Applications/Unity/Unity.app/Contents")
base = root / "build/composed/base"
final = root / "build/composed/final"
base.mkdir(parents=True, exist_ok=True)
final.mkdir(parents=True, exist_ok=True)
for source in (root / "audit-managed/ported").glob("*.dll"):
    shutil.copy2(source, base / source.name)
for source in [
    root / "build/video/managed/NativeH3VR.Game.dll",
    root / "build/video/managed/NativeH3VR.Platform.dll",
    root / "build/fmod/managed/NativeH3VR.FirstPass.dll",
    root / "build/ugui/runtime/UnityEngine.UI.dll",
]:
    shutil.copy2(source, base / source.name)
for source in base.glob("*.dll"):
    shutil.copy2(source, final / source.name)
subprocess.run(
    [
        "python3",
        str(root / "native/build_managed.py"),
        "--input",
        str(base),
        "--output",
        str(final),
        "--unity-contents",
        str(unity),
    ],
    cwd=root,
    check=True,
)
patches = [
    ("platform/optional-hardware/patch/Patch.csproj", "NativeH3VR.FirstPass.dll"),
    ("platform/audio-spatial/patch/Patch.csproj", "NativeH3VR.FirstPass.dll"),
    ("platform/steamworks/lifecycle-patch/Patch.csproj", "NativeH3VR.Game.dll"),
    ("platform/editor-cleanup/patch/Patch.csproj", "NativeH3VR.Game.dll"),
]
for patch, name in patches:
    temporary = final / ("patched-" + name)
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(root / patch),
            "-c",
            "Release",
            "--",
            str(final / name),
            str(temporary),
        ],
        cwd=root,
        env=dict(os.environ, UNITY_CONTENTS=str(unity)),
        check=True,
    )
    temporary.replace(final / name)
temporary = final / "patched-NativeH3VR.Game.dll"
subprocess.run(
    [
        "dotnet",
        "run",
        "--project",
        str(root / "platform/unity2021-patch/Patch.csproj"),
        "-c",
        "Release",
        "--",
        str(final / "NativeH3VR.Game.dll"),
        str(temporary),
        str(unity),
    ],
    cwd=root,
    check=True,
)
temporary.replace(final / "NativeH3VR.Game.dll")
subprocess.run(
    [
        "dotnet",
        "run",
        "--project",
        str(root / "audit-managed/verify/Verify.csproj"),
        "-c",
        "Release",
        "--",
        str(final),
        str(unity),
    ],
    cwd=root,
    check=True,
)
report = {
    "unity_contents": str(unity),
    "target_api_patch": True,
    "order": [
        "Unity API retarget",
        "Steam Darwin ABI",
        "FMOD ABI routing",
        "AVPro native backend",
        "official Unity UI",
        "native SteamVR adapters",
        "optional Windows hardware JIT isolation",
        "FMOD macOS platform",
        "Steam shutdown lifecycle",
        "editor-only post-processing cleanup",
    ]
    + ["target Unity custom reflection API"],
    "files": {
        p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(final.glob("*.dll"))
    },
}
(final / "composition.json").write_text(json.dumps(report, indent=2) + "\n")
print(final)
