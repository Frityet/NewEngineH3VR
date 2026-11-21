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


def check_timings(timing, unity_trace, minimum_samples=80):
    profile = json.loads(timing.read_text())
    for metric in ("unity_eye_render_cpu", "unity_metal_command_buffer", "macvr_gpu_completion_interval"):
        sample = profile["timings"][metric]
        if not sample or sample["samples"] < minimum_samples:
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
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--profile", action="store_true", help="Capture and time the three URP presets")
    modes.add_argument("--swatches", action="store_true", help="Render stock materials with controlled lighting")
    modes.add_argument("--thermal", action="store_true", help="Capture a stocked handheld thermal sensor and display")
    parser.add_argument("--swatch-bundles", nargs="?", const="1", metavar="BUNDLE,...",
                        help="Include materials from all stock asset bundles or a comma-separated subset")
    parser.add_argument("--builtin", action="store_true", help="Profile the original Built-in renderer")
    parser.add_argument("--scene", action="append", default=[], help="Also capture a named stock scene (repeatable)")
    parser.add_argument("--tnh", metavar="MAP", help="Profile one stock Take & Hold map from a fresh session")
    parser.add_argument("--tnh-take-only", action="store_true", help="Finish after the opening Take capture")
    parser.add_argument("--inspect-cover", action="store_true", help="Diagnose the raised steel cover at the first Hold")
    parser.add_argument("--inspect-take-material", metavar="NAME", action="append", default=[],
                        help="Mark an exact material name or prefix* in the opening Take and capture lighting controls (repeatable)")
    parser.add_argument("--tnh-seed", type=int, metavar="ID", help="Stock Take & Hold point sequence ID (default 0)")
    parser.add_argument("--tnh-observer-from", type=Path, metavar="HOLD_STATE_JSON",
                        help="Replay the stock floor marker and heading from an earlier Hold capture")
    parser.add_argument("--preset", choices=("balanced", "quality"), help="Graphics preset for a Take & Hold session")
    args = parser.parse_args()
    if (args.builtin or args.tnh or args.preset or args.tnh_seed is not None) and not args.profile:
        parser.error("--builtin, --tnh and --preset require --profile")
    if args.scene and not (args.profile or args.swatches):
        parser.error("--scene requires --profile or --swatches")
    if args.swatch_bundles and not args.swatches:
        parser.error("--swatch-bundles requires --swatches")
    if "*" in args.scene and (args.scene != ["*"] or not args.swatches):
        parser.error("Use --swatches --scene '*' to render all levels")
    if args.tnh and (args.scene or args.builtin == bool(args.preset)):
        parser.error("--tnh requires either --builtin or --preset, and cannot be used with --scene")
    if args.preset and not args.tnh:
        parser.error("--preset requires --tnh")
    if args.inspect_cover and not args.tnh:
        parser.error("--inspect-cover requires --tnh")
    if args.tnh_take_only and (not args.tnh or args.inspect_cover):
        parser.error("--tnh-take-only requires --tnh and cannot be combined with --inspect-cover")
    if args.inspect_take_material and (not args.tnh or any(
            not name.rstrip("*") or "\n" in name or "*" in name[:-1] for name in args.inspect_take_material)):
        parser.error("--inspect-take-material requires --tnh and a name or prefix with an optional trailing *")
    if args.tnh_seed is not None and (not args.tnh or args.tnh_seed < 0):
        parser.error("--tnh-seed requires --tnh and a nonnegative sequence ID")
    if args.tnh_observer_from and (not args.tnh or not args.tnh_observer_from.is_file()):
        parser.error("--tnh-observer-from requires --tnh and an existing Hold state file")
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
    witness = "GraphicsOptionsWitness"
    if args.profile:
        witness = "GraphicsProfileWitness"
    if args.swatches:
        witness = "MaterialSwatches"
    if args.thermal:
        witness = "RetailThermalWitness"
    manifest = {"schemaVersion": 2, "id": package.name, "unityVersion": settings["unityVersion"],
                "buildTarget": "StandaloneOSX", "hostBuildSha256": digest(receipt),
                "assemblies": [{"file": assembly, "sha256": digest(package / assembly)}],
                "entryPoints": [{"assembly": assembly, "type": "NativeH3VR.QA." + witness, "method": "Initialize"}]}
    (package / "native-mod.json").write_text(json.dumps(manifest, indent=2))

    launch = ["dotnet", str(launcher), "run", "--app", str(app), "--runtime", str(runtime),
              "--mods-dir", str(package.parent), "--state-dir", str(out / "state"), "--synthetic"]
    if args.thermal:
        env = dict(os.environ, NATIVEH3VR_THERMAL_WITNESS=str(out))
        subprocess.run([*launch, "--log-file", str(out / "thermal.log"), "--timeout", "180"],
                       env=env, check=True)
        if not (out / "thermal.json").is_file():
            raise RuntimeError(f"Thermal witness did not complete. Inspect {out / 'thermal.log'}")
        return
    if args.swatches:
        scenes = ["*"] if args.scene == ["*"] else list(dict.fromkeys(["MainMenu3", *args.scene]))
        env = dict(os.environ, NATIVEH3VR_SHADER_SWATCHES=str(out), NATIVEH3VR_SWATCH_SCENES=",".join(scenes),
                   NATIVEH3VR_SWATCH_BUNDLES=args.swatch_bundles or "")
        timeout = 7200 if "*" in scenes or args.swatch_bundles else 240 + 90 * len(scenes)
        subprocess.run([*launch, "--log-file", str(out / "swatches.log"), "--timeout", str(timeout)],
                       env=env, check=True)
        if not (out / "swatches-complete.txt").is_file():
            raise RuntimeError(f"Material swatches did not complete. Inspect {out / 'swatches.log'}")
        return
    if args.profile:
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_PROFILE_WITNESS=str(out),
                   NATIVEH3VR_GRAPHICS_PROFILE_BUILTIN=str(int(args.builtin)),
                   NATIVEH3VR_GRAPHICS_PROFILE_SCENE=",".join(args.scene),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH=args.tnh or "",
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_TAKE_ONLY=str(int(args.tnh_take_only)),
                   NATIVEH3VR_COVER_WITNESS=str(int(args.inspect_cover)),
                   NATIVEH3VR_TAKE_MATERIALS="\n".join(args.inspect_take_material),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_SEED=str(args.tnh_seed or 0),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_OBSERVER=str(args.tnh_observer_from.resolve()) if args.tnh_observer_from else "",
                   NATIVEH3VR_GRAPHICS_PROFILE_PRESET=args.preset or "")
        timeout = 620 if args.tnh else 330
        subprocess.run([*launch, "--log-file", str(out / "profile.log"), "--timeout", str(timeout),
                        "--trace-file", str(out / "openxr.csv"), "--unity-profile-file", str(out / "unity.csv")],
                       env=env, check=True)
        if not (out / "profile-complete.txt").is_file():
            raise RuntimeError(f"Graphics profile witness did not complete. Inspect {out / 'profile.log'}")
        frame_windows = sorted(out.glob("*-frames.txt"))
        if args.tnh:
            phases = ["take"] if args.tnh_take_only else ["hold", "take"]
            if len(frame_windows) != len(phases) or any(
                f"-{phase}-" not in window.name for phase, window in zip(phases, frame_windows)
            ):
                raise RuntimeError(f"Take & Hold witness is missing required windows: {', '.join(phases)}")
        for frames in frame_windows:
            timing = frames.with_name(frames.name.removesuffix("-frames.txt") + "-timings.json")
            with timing.open("w") as file:
                subprocess.run(["dotnet", str(launcher), "profile", "--unity", str(out / "unity.csv"),
                                "--openxr", str(out / "openxr.csv"), "--warmup-seconds", "0", "--unity-frames",
                                *frames.read_text().split()], stdout=file, check=True)
            check_timings(timing, out / "unity.csv", 240 if args.tnh else 80)
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
