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


def completed_swatch_scopes(out):
    scopes = set()
    for path in out.glob("*.json"):
        if path.name in ("candidate-shader.json", "swatches-retries.json"):
            continue
        index = json.loads(path.read_text())
        if index["scene"] != path.stem:
            raise RuntimeError(f"Unexpected material chart scope in {path}")
        if path.stem.startswith("bundle-") and index["status"] != "ok":
            raise RuntimeError(f"Asset bundle check failed in {path}: {index['status']}")
        for page in range(index["pages"]):
            image = out / f"{path.stem}-{page:03}.png"
            with image.open("rb") as stream:
                if stream.read(8) != b"\x89PNG\r\n\x1a\n":
                    raise RuntimeError(f"Invalid material chart PNG {image}")
                stream.seek(-12, os.SEEK_END)
                if stream.read(12) != b"\0\0\0\0IEND\xaeB\x60\x82":
                    raise RuntimeError(f"Incomplete material chart PNG {image}")
        scopes.add(path.stem)
    return scopes


def run_swatches(launch, env, out, timeout):
    log = out / "swatches.log"
    completed = None
    retries = []
    for attempt in range(1, 9):
        result = subprocess.run([*launch, "--log-file", str(log), "--timeout", str(timeout)], env=env)
        if result.returncode == 0:
            if not (out / "swatches-complete.txt").is_file():
                raise RuntimeError(f"Material swatches did not complete. Inspect {log}")
            completed_swatch_scopes(out)
            return
        # Retry only Unity's observed Metal present assertion. Managed errors,
        # timeouts, and other native failures still fail the original command.
        if (result.returncode != 134 or attempt == 8 or not log.is_file()
                or "A command encoder is already encoding to this command buffer"
                not in log.read_text(errors="replace") or (out / "swatches-failed.txt").exists()):
            result.check_returncode()
        scopes = completed_swatch_scopes(out)
        if scopes == completed:
            result.check_returncode()
        completed = scopes
        archive = out / f"swatches-present-failure-{attempt:02}.log"
        log.rename(archive)
        retries.append({"log": archive.name, "completedScopes": sorted(completed)})
        (out / "swatches-retries.json").write_text(json.dumps(retries, indent=2) + "\n")
        # An assertion after the quit receipt still needs a clean player exit.
        (out / "swatches-complete.txt").unlink(missing_ok=True)
        env = dict(env, NATIVEH3VR_SWATCH_COMPLETED_SCOPES="\n".join(sorted(completed)))


def check_timings(timing, unity_trace, minimum_samples=80, headset=False, headset_home=False, expected_hz=None):
    profile = json.loads(timing.read_text())
    if expected_hz and abs(profile["headset_budget_ms"] - 1000 / expected_hz) > 0.05:
        raise RuntimeError(f"OpenXR trace did not use the requested {expected_hz} Hz display period in {timing}")
    for metric in ("unity_eye_render_cpu", "unity_metal_command_buffer", "macvr_gpu_completion_interval"):
        sample = profile["timings"][metric]
        if not sample or sample["samples"] < minimum_samples:
            raise RuntimeError(f"Too few paired stereo samples for {metric} in {timing}")
    if headset:
        profile["headset_route"] = "direct-native"
        if profile["native_display_gpu_completions"] < minimum_samples:
            if not headset_home:
                raise RuntimeError(f"Too few native display GPU completions in {timing}")
            stem = timing.name.removesuffix("-timings.json")
            edges = [json.loads(timing.with_name(f"{stem}-hardware-{edge}.json").read_text())
                     for edge in ("begin", "end")]
            if not all(edge["homeOwnsPlayerView"] and edge["focused"]
                       and abs(edge["displayPeriod"] * 1e-6 - profile["headset_budget_ms"]) < .05
                       for edge in edges):
                raise RuntimeError(f"Missing focused MacVR Home client and matching OpenXR pacing in {timing}")
            profile["headset_route"] = "macvr-home-client"
        profile["physical_scanout_verified"] = False
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
        "macvr_missed_wait_slots": ("MacVR-reported wait slots missed within the profile window"
                                   if headset else "Synthetic runtime wait slots missed within the profile window"),
        "unity_stereo_skipped": "Change in the Unity OpenXR bridge's skipped-frame counter within the profile window",
    }
    timing.write_text(json.dumps(profile, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("app", "runtime", "launcher", "out"):
        parser.add_argument("--" + key, required=True, type=Path)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--sosig-shadow", action="store_true",
                       help="Check a stock live Sosig's shadow using matched native eye captures")
    parser.add_argument("--sosig-contact", action="store_true",
                        help="With --sosig-shadow, isolate the capsule/contact pass instead of native caster flags")
    modes.add_argument("--shadow-motion", action="store_true",
                       help="Check the Northest Dakota snow shadow in both native eyes at fixed head poses")
    modes.add_argument("--profile", action="store_true", help="Capture and time the renderer's graphics presets")
    modes.add_argument("--swatches", action="store_true", help="Render stock materials with controlled lighting")
    modes.add_argument("--thermal", action="store_true", help="Capture a stocked handheld thermal sensor and display")
    modes.add_argument("--optics", action="store_true", help="Read all eight stock optic passes with the overlay off and on")
    modes.add_argument("--aim-optics", action="store_true", help="Check a stock scope and a red dot in both rendered URP eyes")
    modes.add_argument("--vertex-particles", action="store_true",
                       help="Check the stocked legacy vertex-lit particle on Metal")
    parser.add_argument("--swatch-bundles", nargs="?", const="1", metavar="BUNDLE,...",
                        help="Include materials from all stock asset bundles or a comma-separated subset")
    parser.add_argument("--swatch-replacement", nargs=3, metavar=("BUNDLE", "MATERIAL_SHADER", "CANDIDATE_SHADER"),
                        help="Draw the named family's chart materials with a shader from a local test bundle")
    parser.add_argument("--builtin", action="store_true", help="Profile the original Built-in renderer")
    profiles = {"base": "Base.bundle", "builtin": "BuiltIn.bundle", "urp": "URP.bundle"}
    parser.add_argument("--renderer", choices=profiles, help="Select a renderer from a combined NativeH3VR app")
    parser.add_argument("--scene", action="append", default=[], help="Also capture a named stock scene (repeatable)")
    parser.add_argument("--tnh", metavar="MAP", help="Profile one stock Take & Hold map from a fresh session")
    parser.add_argument("--headset", action="store_true", help="Profile through the physical headset")
    parser.add_argument("--headset-home", action="store_true",
                        help="Allow a verified focused MacVR Home client to report game-side timing")
    parser.add_argument("--expect-headset-hz", type=int, choices=(80, 90, 120, 144),
                        help="Require the selected physical headset rate using MacVR's automatic display clock")
    parser.add_argument("--tnh-take-only", action="store_true", help="Finish after the opening Take capture")
    parser.add_argument("--profile-pairs", "--tnh-pairs", type=int, metavar="N",
                        help="Measure N stereo pairs per window (default 100 per preset or 270 per Take & Hold phase)")
    parser.add_argument("--inspect-cover", action="store_true", help="Diagnose the raised steel cover at the first Hold")
    parser.add_argument("--inspect-take-material", metavar="NAME", action="append", default=[],
                        help="Mark an exact material name or prefix* in the opening Take and capture lighting controls (repeatable)")
    parser.add_argument("--tnh-seed", type=int, metavar="ID", help="Stock Take & Hold point sequence ID (default 0)")
    parser.add_argument("--tnh-observer-from", type=Path, metavar="HOLD_STATE_JSON",
                        help="Replay the stock floor marker and heading from an earlier Hold capture")
    parser.add_argument("--preset", choices=("balanced", "quality", "ultra"),
                        help="Graphics preset for a Take & Hold session")
    parser.add_argument("--eye-size", nargs=2, type=int, metavar=("WIDTH", "HEIGHT"),
                        help="Eye size in pixels (URP defaults to 1440 × 1600)")
    args = parser.parse_args()
    if args.sosig_contact and not args.sosig_shadow:
        parser.error("--sosig-contact requires --sosig-shadow")
    if args.builtin and args.renderer == "urp":
        parser.error("--builtin cannot be used with an SRP renderer")
    builtin = args.builtin or args.renderer in ("base", "builtin")
    if (args.builtin or args.tnh or args.preset or args.tnh_seed is not None) and not (args.profile or args.sosig_shadow):
        parser.error("--builtin, --tnh and --preset require --profile or --sosig-shadow")
    if args.scene and not (args.profile or args.swatches):
        parser.error("--scene requires --profile or --swatches")
    if args.swatch_bundles and not args.swatches:
        parser.error("--swatch-bundles requires --swatches")
    if args.swatch_replacement and (not args.swatches or not Path(args.swatch_replacement[0]).is_file()):
        parser.error("--swatch-replacement requires --swatches and an existing shader bundle")
    if "*" in args.scene and (args.scene != ["*"] or not args.swatches):
        parser.error("Use --swatches --scene '*' to render all levels")
    if args.tnh and not args.sosig_shadow and (args.scene or builtin == bool(args.preset)):
        parser.error("--tnh requires a Built-in renderer or --preset, and cannot be used with --scene")
    if args.preset and not (args.tnh or args.sosig_shadow):
        parser.error("--preset requires --tnh")
    if args.eye_size and (not args.profile or min(args.eye_size) <= 0):
        parser.error("--eye-size requires positive dimensions for a profile")
    if args.inspect_cover and not args.tnh:
        parser.error("--inspect-cover requires --tnh")
    if args.headset_home and not args.headset:
        parser.error("--headset-home requires --headset")
    if args.expect_headset_hz and not args.headset:
        parser.error("--expect-headset-hz requires --headset")
    if args.headset and (not args.profile or args.inspect_cover or args.inspect_take_material):
        parser.error("--headset requires --profile and cannot be combined with the material or cover controls")
    if args.tnh_take_only and (not args.tnh or args.inspect_cover):
        parser.error("--tnh-take-only requires --tnh and cannot be combined with --inspect-cover")
    if args.profile_pairs is not None and (not args.profile or not 100 <= args.profile_pairs <= 2**31 - 1):
        parser.error("--profile-pairs requires --profile and a positive 32-bit count of at least 100")
    if args.inspect_take_material and (not args.tnh or any(
            not name.rstrip("*") or "\n" in name or "*" in name[:-1] for name in args.inspect_take_material)):
        parser.error("--inspect-take-material requires --tnh and a name or prefix with an optional trailing *")
    if args.tnh_seed is not None and (not (args.tnh or args.sosig_shadow) or args.tnh_seed < 0):
        parser.error("--tnh-seed requires --tnh and a nonnegative sequence ID")
    if args.tnh_observer_from and (not args.tnh or not args.tnh_observer_from.is_file()):
        parser.error("--tnh-observer-from requires --tnh and an existing Hold state file")
    app, runtime, launcher, out = (p.resolve() for p in (args.app, args.runtime, args.launcher, args.out))
    if out.exists():
        parser.error("--out must be a fresh directory for isolated player settings")
    profile = app / "Contents/Helpers" / profiles[args.renderer] if args.renderer else app
    data = profile / "Contents/Resources/Data"
    receipt = data.parent / "nativeh3vr-build.json"
    settings = json.loads(receipt.read_text())
    urp = (settings.get("graphicsPipeline") or "").startswith("URP ")
    if args.sosig_shadow and not urp:
        parser.error("--sosig-shadow requires a URP build")
    if args.shadow_motion and not urp:
        parser.error("--shadow-motion requires a URP build")
    if args.aim_optics and not urp:
        parser.error("--aim-optics requires a URP build")
    if args.renderer == "urp" and not urp:
        parser.error("The selected URP profile must contain a URP build receipt")
    if urp and builtin:
        parser.error("--builtin requires a Built-in build")
    if (args.preset or args.profile and not builtin) and not urp:
        parser.error("URP profiling requires a URP build")
    if not any((args.profile, args.swatches, args.thermal, args.optics, args.vertex_particles)) and not urp:
        parser.error("The Graphics Options wrist witness requires a URP build")
    assembly = "NativeH3VR.GraphicsOptionsWitness.dll"
    package = out / "mods/nativeh3vr.graphics-options-witness"
    subprocess.run(["dotnet", "build", str(Path(__file__).with_name("GraphicsOptionsWitness.csproj")),
                    "-c", "Release", "-p:NativeH3VRManaged=" + str(data / "Managed"), "--output", str(package),
                    "--nologo", "-v:q"], check=True)
    witness = "GraphicsOptionsWitness"
    if args.profile or args.shadow_motion or args.sosig_shadow or args.aim_optics:
        witness = "GraphicsProfileWitness"
    if args.swatches:
        witness = "MaterialSwatches"
    if args.thermal:
        witness = "RetailThermalWitness"
    if args.optics:
        witness = "OpticBlitWitness"
    if args.vertex_particles:
        witness = "VertexLitParticleWitness"
    manifest = {"schemaVersion": 2, "id": package.name, "unityVersion": settings["unityVersion"],
                "buildTarget": "StandaloneOSX", "hostBuildSha256": digest(receipt),
                "assemblies": [{"file": assembly, "sha256": digest(package / assembly)}],
                "entryPoints": [{"assembly": assembly, "type": "NativeH3VR.QA." + witness, "method": "Initialize"}]}
    (package / "native-mod.json").write_text(json.dumps(manifest, indent=2))

    launch = ["dotnet", str(launcher), "run", "--app", str(app), "--runtime", str(runtime),
              "--mods-dir", str(package.parent), "--state-dir", str(out / "state"), "--headset" if args.headset else "--synthetic"]
    if args.renderer:
        launch += ["--renderer", args.renderer]
    if args.sosig_shadow:
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_PROFILE_WITNESS=str(out),
                   NATIVEH3VR_SOSIG_SHADOW_WITNESS="1", NATIVEH3VR_SOSIG_CONTACT_WITNESS=str(int(args.sosig_contact)),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH=args.tnh or "NorthestDakota",
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_SEED=str(args.tnh_seed or 0),
                   NATIVEH3VR_GRAPHICS_PROFILE_PRESET=args.preset or "ultra", VR_OPENXR_TARGET_HZ="90",
                   NATIVEH3VR_EYE_WIDTH="1440", NATIVEH3VR_EYE_HEIGHT="1600")
        subprocess.run([*launch, "--log-file", str(out / "sosig-shadow.log"), "--timeout", "360"],
                       env=env, check=True)
        report = out / "sosig-shadow.json"
        if not report.is_file() or not json.loads(report.read_text()).get("passed"):
            raise RuntimeError(f"Sosig-shadow witness did not pass. Inspect {report}")
        print(report)
        return
    if args.shadow_motion:
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_PROFILE_WITNESS=str(out),
                   NATIVEH3VR_SHADOW_MOTION_WITNESS="1", VR_OPENXR_TARGET_HZ="90",
                   NATIVEH3VR_EYE_WIDTH="1440", NATIVEH3VR_EYE_HEIGHT="1600")
        subprocess.run([*launch, "--log-file", str(out / "shadow-motion.log"), "--timeout", "180"],
                       env=env, check=True)
        for name in ("shadow-motion.json", "shadow-motion-tree.json"):
            report = out / name
            if not report.is_file() or not json.loads(report.read_text()).get("passed"):
                raise RuntimeError(f"Shadow-motion witness did not pass. Inspect {report}")
            print(report)
        return

    if args.vertex_particles:
        env = dict(os.environ, NATIVEH3VR_VERTEX_PARTICLE_WITNESS=str(out),
                   NATIVEH3VR_EXPECT_STOCK_VERTEX_PARTICLES="1")
        subprocess.run([*launch, "--log-file", str(out / "vertex-particles.log"), "--timeout", "100"],
                       env=env, check=True)
        if not (out / "complete.txt").is_file() or not (out / "installed.png").is_file():
            raise RuntimeError(f"Particle witness did not complete. Inspect {out / 'vertex-particles.log'}")
        return
    if args.optics:
        env = dict(os.environ, NATIVEH3VR_OPTIC_BLIT_WITNESS=str(out))
        subprocess.run([*launch, "--log-file", str(out / "optics.log"), "--timeout", "220"], env=env, check=True)
        if not (out / "inputs.txt").is_file() or not (out / "passes.txt").is_file() or len(list(out.glob("*.png"))) != 16:
            raise RuntimeError(f"Optic pass witness did not finish all 16 readbacks. Inspect {out / 'optics.log'}")
        return
    if args.aim_optics:
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_PROFILE_WITNESS=str(out), NATIVEH3VR_AIMED_OPTICS_WITNESS="1",
                   NATIVEH3VR_EYE_WIDTH="1440", NATIVEH3VR_EYE_HEIGHT="1600", VR_OPENXR_TARGET_HZ="90")
        subprocess.run([*launch, "--log-file", str(out / "aimed-optics.log"), "--timeout", "260"],
                       env=env, check=True)
        if not (out / "profile-complete.txt").is_file() or not (out / "aimed-optics.json").is_file():
            raise RuntimeError(f"Aimed optics witness did not finish. Inspect {out / 'aimed-optics.log'}")
        return
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
        env.pop("NATIVEH3VR_SWATCH_COMPLETED_SCOPES", None)
        if args.swatch_replacement:
            path, family, shader = args.swatch_replacement
            bundle = Path(path).resolve()
            env.update(NATIVEH3VR_SWATCH_CANDIDATE_BUNDLE=str(bundle), NATIVEH3VR_SWATCH_REPLACED_FAMILY=family,
                       NATIVEH3VR_SWATCH_CANDIDATE_NAME=shader)
            proof = {"bundleSha256": digest(bundle), "materialShader": family, "candidateShader": shader,
                     "hostBuildSha256": digest(receipt)}
            (out / "candidate-shader.json").write_text(json.dumps(proof, indent=2))
        timeout = 7200 if "*" in scenes or args.swatch_bundles else 240 + 90 * len(scenes)
        run_swatches(launch, env, out, timeout)
        return
    if args.profile:
        profile_pairs = args.profile_pairs or (270 if args.tnh else 100)
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_PROFILE_WITNESS=str(out),
                   NATIVEH3VR_GRAPHICS_PROFILE_BUILTIN=str(int(builtin)),
                   NATIVEH3VR_GRAPHICS_PROFILE_HEADSET=str(int(args.headset)),
                   NATIVEH3VR_GRAPHICS_PROFILE_SCENE=",".join(args.scene),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH=args.tnh or "",
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_TAKE_ONLY=str(int(args.tnh_take_only)),
                   NATIVEH3VR_GRAPHICS_PROFILE_PAIRS=str(profile_pairs),
                   NATIVEH3VR_COVER_WITNESS=str(int(args.inspect_cover)),
                   NATIVEH3VR_TAKE_MATERIALS="\n".join(args.inspect_take_material),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_SEED=str(args.tnh_seed or 0),
                   NATIVEH3VR_GRAPHICS_PROFILE_TNH_OBSERVER=str(args.tnh_observer_from.resolve()) if args.tnh_observer_from else "",
                   NATIVEH3VR_GRAPHICS_PROFILE_PRESET=args.preset or "")
        synthetic_urp = urp and not args.headset
        eye_size = args.eye_size or ((1440, 1600) if urp else None)
        if synthetic_urp:
            env["VR_OPENXR_TARGET_HZ"] = "90"
        elif args.expect_headset_hz:
            # A fixed OpenXR cadence can report the requested rate while the
            # physical headset or the MacVR Home owner uses a different clock.
            env["VR_OPENXR_TARGET_HZ"] = "auto"
        if eye_size:
            for dimension, size in zip(("WIDTH", "HEIGHT"), eye_size):
                env["NATIVEH3VR_EYE_" + dimension] = str(size)
            env["NATIVEH3VR_GRAPHICS_PROFILE_EYE_SIZE"] = " ".join(map(str, eye_size))
        else:
            env.pop("NATIVEH3VR_GRAPHICS_PROFILE_EYE_SIZE", None)
        timeout = 780 if args.tnh else 180 + 480 * len({"MainMenu3", *args.scene})
        if args.profile_pairs:
            windows = 2 if args.tnh else 3 * len({"MainMenu3", *args.scene})
            timeout += windows * profile_pairs // 10
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
        elif urp:
            expected = {f"{scene}-{preset}-frames.txt" for scene in {"MainMenu3", *args.scene}
                        for preset in ("balanced", "quality", "ultra")}
            if {path.name for path in frame_windows} != expected:
                raise RuntimeError("URP profile did not complete every requested scene and preset window")
        for frames in frame_windows:
            timing = frames.with_name(frames.name.removesuffix("-frames.txt") + "-timings.json")
            with timing.open("w") as file:
                subprocess.run(["dotnet", str(launcher), "profile", "--unity", str(out / "unity.csv"),
                                "--openxr", str(out / "openxr.csv"), "--warmup-seconds", "0", "--unity-frames",
                                *frames.read_text().split()], stdout=file, check=True)
            minimum = max(80, profile_pairs - 30)
            check_timings(timing, out / "unity.csv", minimum, args.headset, args.headset_home,
                          expected_hz=90 if synthetic_urp else args.expect_headset_hz)
        print(out)
        return

    for phase in ("interaction", "restart"):
        env = dict(os.environ, NATIVEH3VR_GRAPHICS_UI_WITNESS=str(out),
                   NATIVEH3VR_EYE_WIDTH="1440", NATIVEH3VR_EYE_HEIGHT="1600",
                   NATIVEH3VR_GRAPHICS_UI_RESTART=str(int(phase == "restart")))
        subprocess.run([*launch, "--log-file", str(out / f"{phase}.log"), "--timeout", "330"],
                       env=env, check=True)
        if not (out / f"{phase}-complete.txt").is_file():
            raise RuntimeError(f"Graphics Options {phase} witness did not complete. Inspect {out / (phase + '.log')}")
    for image in ("native-graphics.png", "effects-graphics.png", "game-graphics.png"):
        if not (out / image).is_file():
            raise RuntimeError(f"Graphics Options witness did not create {image}")
    print(out)


if __name__ == "__main__":
    main()
