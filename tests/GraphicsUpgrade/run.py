#!/usr/bin/env python3
"""Run native Graphics Options or stereo preset diagnostics in an imported player."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_timings(timing, unity_trace):
    profile = json.loads(timing.read_text())
    for metric in ("unity_eye_render_cpu", "unity_metal_command_buffer", "macvr_gpu_completion_interval"):
        sample = profile["timings"][metric]
        if not sample or sample["samples"] < 80:
            raise RuntimeError(f"Too few paired stereo samples for {metric} in {timing}")
    start, end = profile["window_display_start_ns"], profile["window_display_end_ns"]
    with unity_trace.open(newline="") as file:
        skipped = [int(row["stereo_skipped"]) for row in csv.DictReader(file)
                   if start <= int(row["xr_display_ns"]) <= end]
    if len(skipped) < 2:
        raise RuntimeError(f"No Unity skip counter for {timing}")
    profile["unity_stereo_skipped"] = skipped[-1] - skipped[0]
    profile["timing_scope"] = {
        "unity_eye_render_cpu": "CPU duration of both eye render invocations and submit scheduling",
        "unity_metal_command_buffer": "Metal command buffer containing the Unity OpenXR plugin event",
        "macvr_metal_gpu": "MacVR compositor command buffer",
        "macvr_gpu_completion_interval": "Monotonic elapsed time between completed compositor callbacks",
        "macvr_missed_wait_slots": "Synthetic runtime wait slots missed within the profile window",
        "unity_stereo_skipped": "Change in the Unity OpenXR bridge's skipped-frame counter within the profile window",
    }
    timing.write_text(json.dumps(profile, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("app", "runtime", "launcher", "out"):
        parser.add_argument("--" + key, required=True, type=Path)
    parser.add_argument("--profile", action="store_true", help="Capture and time the three URP presets")
    parser.add_argument("--builtin", action="store_true", help="Profile the original Built-in renderer")
    parser.add_argument("--scene", help="Also capture a named stock scene")
    args = parser.parse_args()
    if (args.builtin or args.scene) and not args.profile:
        parser.error("--builtin and --scene require --profile")
    app, runtime, launcher, out = (p.resolve() for p in (args.app, args.runtime, args.launcher, args.out))
    if out.exists():
        parser.error("--out must be a fresh directory for isolated player settings")
    data = app / "Contents/Resources/Data"
    receipt = data.parent / "nativeh3vr-build.json"
    settings = json.loads(receipt.read_text())
    assembly = "NativeH3VR.GraphicsOptionsWitness.dll"
    package = out / "mods/nativeh3vr.graphics-options-witness"
    subprocess.run(["dotnet", "build", str(Path(__file__).with_name("GraphicsOptionsWitness.csproj")),
                    "-c", "Release", "-p:NativeH3VRManaged=" + str(data / "Managed"), "--output", str(package),
                    "--nologo", "-v:q"], check=True)
    witness = "GraphicsProfileWitness" if args.profile else "GraphicsOptionsWitness"
    manifest = {"schemaVersion": 2, "id": package.name, "unityVersion": settings["unityVersion"],
                "buildTarget": "StandaloneOSX", "hostBuildSha256": digest(receipt),
                "assemblies": [{"file": assembly, "sha256": digest(package / assembly)}],
                "entryPoints": [{"assembly": assembly, "type": "NativeH3VR.QA." + witness, "method": "Initialize"}]}
    (package / "native-mod.json").write_text(json.dumps(manifest, indent=2))

    launch = ["dotnet", str(launcher), "run", "--app", str(app), "--runtime", str(runtime),
              "--mods-dir", str(package.parent), "--state-dir", str(out / "state"), "--synthetic"]
    if args.profile:
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_PROFILE_WITNESS=str(out),
                   NATIVEH3VR_GRAPHICS_PROFILE_BUILTIN=str(int(args.builtin)),
                   NATIVEH3VR_GRAPHICS_PROFILE_SCENE=args.scene or "")
        subprocess.run([*launch, "--log-file", str(out / "profile.log"), "--timeout", "330",
                        "--trace-file", str(out / "openxr.csv"), "--unity-profile-file", str(out / "unity.csv")],
                       env=env, check=True)
        if not (out / "profile-complete.txt").is_file():
            raise RuntimeError(f"Graphics profile witness did not complete. Inspect {out / 'profile.log'}")
        for frames in sorted(out.glob("*-frames.txt")):
            timing = frames.with_name(frames.name.removesuffix("-frames.txt") + "-timings.json")
            with timing.open("w") as file:
                subprocess.run(["dotnet", str(launcher), "profile", "--unity", str(out / "unity.csv"),
                                "--openxr", str(out / "openxr.csv"), "--warmup-seconds", "0", "--unity-frames",
                                *frames.read_text().split()], stdout=file, check=True)
            check_timings(timing, out / "unity.csv")
        print(out)
        return

    for phase in ("interaction", "restart"):
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_UI_WITNESS=str(out),
                   NATIVEH3VR_GRAPHICS_UI_RESTART=str(int(phase == "restart")))
        subprocess.run([*launch, "--log-file", str(out / f"{phase}.log"), "--timeout", "330"],
                       env=env, check=True)
        if not (out / f"{phase}-complete.txt").is_file():
            raise RuntimeError(f"Graphics Options {phase} witness did not complete. Inspect {out / (phase + '.log')}")
    for image in ("native-graphics.png", "game-graphics.png"):
        if not (out / image).is_file():
            raise RuntimeError(f"Graphics Options witness did not create {image}")
    print(out)


if __name__ == "__main__":
    main()
