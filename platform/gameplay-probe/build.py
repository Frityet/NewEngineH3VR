#!/usr/bin/env python3
"""Compile only the isolated acceptance harness against the selected game/engine."""

import argparse
import pathlib
import plistlib
import subprocess

root = pathlib.Path(__file__).resolve().parent
workspace = root.parents[1]
engine2021 = (
    workspace / ".tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents"
)
engine2020 = pathlib.Path("/Applications/Unity/Unity.app/Contents")
p = argparse.ArgumentParser()
p.add_argument(
    "--managed", type=pathlib.Path, default=workspace / "native/build/ported-vr-estimated"
)
p.add_argument(
    "--unity-contents", type=pathlib.Path, default=engine2021 if engine2021.exists() else engine2020
)
p.add_argument("--output", type=pathlib.Path, default=root / "build/NativeH3VR.GameplayProbe.dll")
args = p.parse_args()
args.output.parent.mkdir(parents=True, exist_ok=True)
# The installed Mono compiler runs natively; references come from the selected
# player engine. Avoid depending on the old editor's Rosetta compiler server.
compiler = engine2020 / "MonoBleedingEdge/bin/mcs"
refs = [
    *(args.unity_contents / "Managed/UnityEngine").glob("UnityEngine*.dll"),
    *args.managed.glob("*.dll"),
]
# A staged Plugins directory can contain an earlier build of this harness.
refs = [path for path in refs if path.name != "NativeH3VR.GameplayProbe.dll"]
# Built-player Managed directories repeat the engine modules. Keep the selected
# engine reference once, while retaining game/plugin assemblies from --managed.
unique_refs = {}
for path in refs:
    unique_refs.setdefault(path.name, path)
refs = list(unique_refs.values())
version = plistlib.loads((args.unity_contents / "Info.plist").read_bytes()).get(
    "CFBundleVersion", ""
)
engine_defines = ["-define:NEWENGINE_UNITY_2021"] if str(version).startswith("2021.") else []
subprocess.run(
    [
        str(compiler),
        "-target:library",
        *engine_defines,
        "-out:" + str(args.output),
        *["-r:" + str(path) for path in refs],
        str(root / "NativeGameplayProbe.cs"),
        str(root / "NativeGameplayBallisticEvidence.cs"),
        str(root / "NativeGameplayLoadFixture.cs"),
        str(root / "NativeGameplayVaultFixture.cs"),
        str(root / "NativePlayerProbeInput.cs"),
        str(root / "NativePlayerInteractionProbe.cs"),
        str(root / "NativePlayerWristFixture.cs"),
        str(root / "NativePlayerQuickbeltFixture.cs"),
        str(root / "NativePerformanceProbe.cs"),
        str(root / "NativeGraphicsTextureProbe.cs"),
        str(root / "NativeScopeProbe.cs"),
        str(root / "NewEngineLiveProfile.cs"),
        str(root / "NativeDemoStartup.cs"),
    ],
    check=True,
)
print(args.output)
print("Unity references:", args.unity_contents)
