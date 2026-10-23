#!/usr/bin/env python3
"""Pure .NET checks and isolated C#6 compile only; never launches Unity or edits an app."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parent
WORKSPACE = ROOT.parents[1]
BASELINES = {
    "NativeSteamVRBootstrap.cs": "3b0831edae76758e24a06c27cae23a73c20fe8c82f7951e95b6b79646c190f0c",
    "NativeSteamVRInput.cs": "93fb5f8396a2ff32b972a06e6a95190d2ed63290ef3599b416cfe313129a31ce",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--managed",
        type=Path,
        default=WORKSPACE / "dist/NewEngineH3VR.app/Contents/Resources/Data/Managed",
    )
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--dotnet", type=Path, default=Path("/opt/homebrew/opt/dotnet/libexec/dotnet")
    )
    parser.add_argument(
        "--unity-contents",
        type=Path,
        default=WORKSPACE
        / ".tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents",
    )
    args = parser.parse_args()
    output = args.output.resolve()
    # Fresh, isolated artifacts only. All game/engine inputs remain read-only.
    if output.exists() or ".app" in str(output) or not output.is_relative_to(WORKSPACE / "build"):
        parser.error("--output must be a fresh directory below this workspace build/")
    output.mkdir(parents=True)
    generated = output / "baseline-source"
    generated.mkdir()
    for name, expected in BASELINES.items():
        source = (ROOT / "baseline" / name).read_bytes()
        if hashlib.sha256(source).hexdigest() != expected:
            raise RuntimeError("Frozen original source changed: " + name)
        # Only rename the containing namespace; method bodies remain original.
        text = source.decode().replace(
            "namespace NativeH3VR\n", "using NativeH3VR;\nnamespace OriginalNativeH3VR\n", 1
        )
        (generated / name).write_text(text)
    env = dict(os.environ, DOTNET_ROOT=str(args.dotnet.parent))
    for candidate in (True, False):
        mode = "candidate" if candidate else "legacy"
        directory = output / mode
        subprocess.run(
            [
                str(args.dotnet),
                "run",
                "--project",
                str(ROOT / "Checks.csproj"),
                "-c",
                "Release",
                "-p:GameManaged=" + str(args.managed.resolve()),
                "-p:GeneratedBaseline=" + str(generated),
                "-p:LookupCandidate=" + str(candidate).lower(),
                "-p:BaseIntermediateOutputPath=" + str(directory / "obj") + "/",
                "-p:OutputPath=" + str(directory / "bin") + "/",
                "--",
                str(output / (mode + ".json")),
            ],
            env=env,
            check=True,
            timeout=120,
        )
    compiler = Path("/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/bin/mcs")
    sources = [
        ROOT.parent / "managed" / name
        for name in [
            "NativeTracking.cs",
            "NativeEstimatedHand.cs",
            "NativeDiagnosticInput.cs",
            "NativeSteamVRInput.cs",
            "NativeSteamVRSystem.cs",
            "NativeSteamVRBootstrap.cs",
        ]
    ]
    references = [
        args.managed / name
        for name in [
            "NativeH3VR.XR.Managed.dll",
            "NativeH3VR.FirstPass.dll",
            "NativeH3VR.Game.dll",
            "Valve.Newtonsoft.Json.dll",
        ]
    ]
    references.append(args.unity_contents / "Managed/UnityEngine/UnityEngine.CoreModule.dll")
    for mode, defines in [("candidate", ["-define:NEWENGINE_UNITY_2021"]), ("legacy", [])]:
        subprocess.run(
            [
                str(compiler),
                "-langversion:6",
                "-target:library",
                *defines,
                "-out:" + str(output / mode / "NativeH3VR.SteamVR.Adapter.dll"),
                *["-r:" + str(path) for path in references],
                *map(str, sources),
            ],
            check=True,
            timeout=120,
        )
    subprocess.run(
        [
            str(args.dotnet),
            "run",
            "--project",
            str(ROOT / "Verify.csproj"),
            "-c",
            "Release",
            "-p:BaseIntermediateOutputPath=" + str(output / "verify-obj") + "/",
            "-p:OutputPath=" + str(output / "verify-bin") + "/",
            "--",
            str(args.managed / "NativeH3VR.SteamVR.Adapter.dll"),
            str(output / "candidate/NativeH3VR.SteamVR.Adapter.dll"),
            str(output / "legacy/NativeH3VR.SteamVR.Adapter.dll"),
            str(output / "il-checks.json"),
        ],
        env=env,
        check=True,
        timeout=120,
    )
    files = [
        *sorted((ROOT.parent / "managed").glob("*.cs")),
        *sorted(ROOT.glob("*.cs")),
        *sorted(ROOT.glob("*.csproj")),
        ROOT / "run.py",
    ]
    receipt = {
        "scope": "source-only pure .NET host tests and C#6 candidate/legacy compilation; no Unity execution or gameplay timing",
        "baselineSha256": BASELINES,
        "sources": {
            str(p.relative_to(WORKSPACE)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files
        },
        "references": {
            str(p.resolve()): hashlib.sha256(p.read_bytes()).hexdigest() for p in references
        },
        "artifacts": {
            str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in output.rglob("*")
            if p.is_file() and p.suffix in (".json", ".dll") and "obj" not in p.parts
        },
    }
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(output)


if __name__ == "__main__":
    main()
