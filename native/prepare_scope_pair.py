#!/usr/bin/env python3
"""Prepare an isolated Unity 2021 scoped stereo + eye-pair lifetime candidate; never install or launch it."""

from pathlib import Path
import argparse
import hashlib
import json
import plistlib
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command):
    subprocess.run([str(p) for p in command], check=True, timeout=60)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-game", type=Path, required=True)
    parser.add_argument("--baseline-xr", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True)
    parser.add_argument("--unity-contents", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    game, old_xr, refs, unity, output = (
        p.resolve()
        for p in [
            args.input_game,
            args.baseline_xr,
            args.references,
            args.unity_contents,
            args.output,
        ]
    )
    if not output.is_relative_to(ROOT / "build") or output.is_relative_to(ROOT / "build/optih3vr"):
        raise ValueError(
            "Candidate output must be under port build/ outside OptiH3VR install state"
        )
    if game.is_relative_to(output) or old_xr.is_relative_to(output) or refs.is_relative_to(output):
        raise ValueError("Candidate output must not contain any input/dependency directory")
    version = plistlib.loads((unity / "Info.plist").read_bytes())["CFBundleVersion"]
    if not version.startswith("2021."):
        raise ValueError("Scoped graphics adaptation requires Unity 2021")
    output.mkdir(parents=True, exist_ok=True)
    mcs = unity / "MonoBleedingEdge/bin/mcs"
    modules = [
        unity / ("Managed/UnityEngine/UnityEngine." + module + ".dll")
        for module in ["CoreModule", "IMGUIModule"]
    ]
    xr = output / "NativeH3VR.XR.Managed.dll"
    compile_command = [
        mcs,
        "-target:library",
        "-define:NEWENGINE_UNITY_2021",
        "-out:" + str(xr),
        *["-r:" + str(p) for p in modules],
        ROOT / "native/managed/NativeH3VRXR.cs",
        ROOT / "native/managed/NativePostProcessStereo.cs",
        ROOT / "native/managed/NativeScopeActivation.cs",
    ]
    run(compile_command)
    project = ROOT / "native/scope-pair-patch/ScopePairPatch.csproj"
    run(["dotnet", "build", project, "-c", "Release", "--ignore-failed-sources"])
    tool = project.parent / "bin/Release/net10.0/ScopePairPatch.dll"
    candidate = output / "NativeH3VR.Game.dll"
    proof = output / "engine-baseline-proof.json"
    tail = [game, xr, candidate, proof, refs, old_xr]
    run(["dotnet", tool, *tail])
    tail[3] = output / "independent-verification.json"
    run(["dotnet", tool, "--verify", *tail])
    receipt = {
        "schema_version": 1,
        "candidate_only": True,
        "engine_version": version,
        "engine_define": "NEWENGINE_UNITY_2021",
        "compiler": str(mcs),
        "compiler_sha256": digest(mcs),
        "compile_command": [str(p) for p in compile_command],
        "references": {str(p): digest(p) for p in modules},
        "input_game": str(game),
        "input_sha256": digest(game),
        "baseline_xr": str(old_xr),
        "baseline_xr_sha256": digest(old_xr),
        "candidate_game_sha256": digest(candidate),
        "candidate_xr_sha256": digest(xr),
        "proof_sha256": digest(proof),
        "independent_verification_sha256": digest(tail[3]),
    }
    (output / "candidate-build.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(
        "Candidate prepared; app, OptiH3VR install state and running processes unchanged:", output
    )


if __name__ == "__main__":
    main()
