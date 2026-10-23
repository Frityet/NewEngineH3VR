#!/usr/bin/env python3
"""Build managed native adapters and guarded patched copies; never edits inputs."""

import pathlib, subprocess, shutil, argparse, hashlib, json, os, plistlib

root = pathlib.Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--input", type=pathlib.Path, default=root.parent / "audit-managed/ported")
parser.add_argument("--output", type=pathlib.Path, default=root / "build/ported-vr")
parser.add_argument(
    "--unity-contents",
    type=pathlib.Path,
    default=pathlib.Path(
        os.environ.get("UNITY_CONTENTS", "/Applications/Unity/Unity.app/Contents")
    ),
)
args = parser.parse_args()
if args.input.resolve() == args.output.resolve():
    raise SystemExit("Input and output must be different: originals are never overwritten.")
args.output.mkdir(parents=True, exist_ok=True)
for dependency in args.input.glob("*.dll"):
    shutil.copy2(dependency, args.output / dependency.name)
editor = args.unity_contents.resolve()
engine_version = plistlib.loads((editor / "Info.plist").read_bytes()).get("CFBundleVersion", "")
engine_defines = ["-define:NEWENGINE_UNITY_2021"] if str(engine_version).startswith("2021.") else []


def run(parts):
    subprocess.run([str(p) for p in parts], check=True, timeout=120)


managed = root / "build/NativeH3VR.XR.Managed.dll"
adapter = root / "build/NativeH3VR.SteamVR.Adapter.dll"
run(
    [
        editor / "MonoBleedingEdge/bin/mcs",
        "-target:library",
        *engine_defines,
        "-out:" + str(managed),
        "-r:" + str(editor / "Managed/UnityEngine/UnityEngine.CoreModule.dll"),
        "-r:" + str(editor / "Managed/UnityEngine/UnityEngine.IMGUIModule.dll"),
        root / "managed/NativeH3VRXR.cs",
        root / "managed/NativePostProcessStereo.cs",
        root / "managed/NativeScopeActivation.cs",
    ]
)
run(
    [
        editor / "MonoBleedingEdge/bin/mcs",
        "-target:library",
        *engine_defines,
        "-out:" + str(adapter),
        "-r:" + str(managed),
        *[
            "-r:" + str(args.input / name)
            for name in [
                "NativeH3VR.FirstPass.dll",
                "NativeH3VR.Game.dll",
                "Valve.Newtonsoft.Json.dll",
            ]
        ],
        "-r:" + str(editor / "Managed/UnityEngine/UnityEngine.CoreModule.dll"),
        *[
            root / "managed" / name
            for name in [
                "NativeTracking.cs",
                "NativeEstimatedHand.cs",
                "NativeDiagnosticInput.cs",
                "NativeSteamVRInput.cs",
                "NativeSteamVRSystem.cs",
                "NativeSteamVRBootstrap.cs",
            ]
        ],
    ]
)
shutil.copy2(managed, args.output / managed.name)
run(
    [
        "dotnet",
        "run",
        "--project",
        root / "patch/Patch.csproj",
        "--",
        args.input,
        adapter,
        args.output,
    ]
)
for name in ["NativeH3VR.XR.Managed.dll", "libNativeH3VRXR.dylib", "libNativeH3VRTracking.dylib"]:
    shutil.copy2(root / "build" / name, args.output / name)
run(
    [
        "dotnet",
        "run",
        "--project",
        root.parent / "platform/managed-local-references/Repair.csproj",
        "-c",
        "Release",
        "--",
        args.output,
    ]
)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


provenance = {
    "input_directory": str(args.input.resolve()),
    "output_directory": str(args.output.resolve()),
    "engine_version": engine_version,
    "engine_defines": engine_defines,
    "input_sha256": {p.name: digest(p) for p in args.input.glob("*.dll")},
    "output_sha256": {
        p.name: digest(p) for p in args.output.iterdir() if p.suffix in [".dll", ".dylib"]
    },
    "source_sha256": {
        str(p.relative_to(root)): digest(p)
        for p in [
            *sorted((root / "managed").glob("*.cs")),
            *sorted((root / "patch").glob("*.cs")),
            root / "build_managed.py",
        ]
    },
}
(args.output / "vr-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
