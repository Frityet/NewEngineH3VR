"""Build the Unity 6.7 validation fixture and run it on a supported desktop host."""

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile


FIXTURE = Path(__file__).resolve().parents[1] / "fixture"
TARGET_VERSION = "6000.7.0b10"
CAPTURES = ("raster.png", "stereo-left.png", "stereo-right.png")
EXPECTED_CHECKS = frozenset({
    "alpha-blend", "back-face-culled", "camera-layer-exclusion", "depth-rejects-later-far-draw",
    "far-draw-remains-visible-around-occluder", "far-plane-clip", "fixed-step-gravity", "front-face-visible",
    "geometry-visible-at-viewport-edge", "graphics-device", "near-plane-clip", "opaque-background-under-blend",
    "parented-transform-round-trip", "physics-contact", "physics-raycast", "point-texture-bottom-left",
    "point-texture-bottom-right", "point-texture-top-left", "point-texture-top-right", "raster-shader",
    "stereo-horizontal-parallax", "stereo-vertical-alignment",
})
MAX_MEAN_CHANNEL_ERROR = 1.0
PIXEL_ERROR_THRESHOLD = 8
MAX_CHANGED_PIXEL_FRACTION = 0.005


class ValidationError(Exception):
    pass


@dataclass(frozen=True)
class Editor:
    executable: Path
    version: str


@dataclass(frozen=True)
class Target:
    unity_target: str
    player_name: str
    graphics: str
    graphics_flag: str
    architecture: str


TARGETS = {
    "macos": Target("OSXUniversal", "EngineValidation.app", "Metal", "-force-metal", "arm64"),
    "windows": Target("Win64", "EngineValidation.exe", "Direct3D11", "-force-d3d11", "x86_64"),
    "linux": Target("Linux64", "EngineValidation.x86_64", "Vulkan", "-force-vulkan", "x86_64"),
}


def default_editor(host):
    if host == "macos":
        return Path("/Applications/Unity/Hub/Editor") / TARGET_VERSION / "Unity.app"
    if host == "windows":
        programs = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
        return programs / "Unity/Hub/Editor" / TARGET_VERSION / "Editor/Unity.exe"
    return Path.home() / "Unity/Hub/Editor" / TARGET_VERSION / "Editor/Unity"


def read_bundle_info(bundle):
    with (bundle / "Contents/Info.plist").open("rb") as info:
        return plistlib.load(info)


def find_editor(location):
    location = location.expanduser().resolve()
    executable = location / "Contents/MacOS/Unity" if location.suffix == ".app" else location
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise ValidationError(f"Unity editor executable unavailable: {executable}")
    try:
        process = subprocess.run([executable, "-version"], capture_output=True, text=True,
                                 errors="replace", timeout=30, check=True)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
        raise ValidationError(f"Could not read Unity editor version at {location}") from error
    versions = re.findall(r"(?m)^\s*(\d{4}\.\d+\.\d+[abfp]\d+)\s*$", process.stdout)
    if len(versions) != 1 or not versions[0].startswith("6000.7."):
        raise ValidationError(f"Expected a Unity 6.7 editor at {location}")
    version = versions[0]
    return Editor(executable, version)


def run_process(command, log, env, timeout):
    with log.open("wb") as output:
        try:
            result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT,
                                    env=env, timeout=timeout, check=False)
        except subprocess.TimeoutExpired as error:
            raise ValidationError(f"Process exceeded {timeout} seconds. See {log}") from error
    if result.returncode != 0:
        raise ValidationError(f"Process exited with status {result.returncode}. See {log.parent}")


def read_report(directory, version, graphics, scripting_backend):
    report = json.loads((directory / "report.json").read_text(encoding="utf-8"))
    if not isinstance(report, dict) or report.get("version") != version:
        raise ValidationError(f"Expected a report from Unity {version} in {directory}")
    if report.get("graphicsDevice") != graphics:
        raise ValidationError(f"Expected {graphics} in {directory}, found {report.get('graphicsDevice')!r}")
    if report.get("scriptingBackend") != scripting_backend:
        raise ValidationError(f"Expected {scripting_backend} in {directory}, found {report.get('scriptingBackend')!r}")
    checks = report.get("checks")
    if not isinstance(checks, list) or not checks:
        raise ValidationError(f"No checks in {directory}")
    names = set()
    for check in checks:
        if not isinstance(check, dict) or not isinstance(check.get("name"), str):
            raise ValidationError(f"Malformed check in {directory}")
        name = check["name"]
        if not name or name in names or check.get("passed") is not True:
            raise ValidationError(f"Invalid or failed check {name!r} in {directory}")
        names.add(name)
    missing = sorted(EXPECTED_CHECKS - names)
    unexpected = sorted(names - EXPECTED_CHECKS)
    if missing or unexpected:
        raise ValidationError(f"Wrong fixture checks in {directory}. Missing {missing}. Unexpected {unexpected}.")
    captures = report.get("captures")
    if report.get("passed") is not True or captures != list(CAPTURES):
        raise ValidationError(f"Fixture failed or did not produce its full report in {directory}")
    for capture in CAPTURES:
        png = directory / capture
        with png.open("rb") as image:
            if image.read(8) != b"\x89PNG\r\n\x1a\n":
                raise ValidationError(f"Invalid PNG capture: {png}")
        read_frame(png.with_suffix(".ppm"))
    return names


def read_frame(path):
    with path.open("rb") as image:
        magic = image.readline(16)
        dimensions = image.readline(32)
        max_sample = image.readline(16)
        match = re.fullmatch(rb"([1-9][0-9]{0,3}) ([1-9][0-9]{0,3})\n", dimensions)
        if magic != b"P6\n" or not match or max_sample != b"255\n":
            raise ValidationError(f"Invalid fixture PPM header: {path}")
        width, height = map(int, match.groups())
        if width * height > 4_194_304:
            raise ValidationError(f"Oversized fixture capture: {path}")
        expected_bytes = width * height * 3
        pixels = image.read(expected_bytes + 1)
        if len(pixels) != expected_bytes:
            raise ValidationError(f"Invalid fixture PPM length: {path}")
    return (width, height), pixels


def compare_frames(baseline, target):
    dimensions, baseline_pixels = read_frame(baseline)
    target_dimensions, target_pixels = read_frame(target)
    if dimensions != target_dimensions:
        raise ValidationError(f"Capture dimensions differ: {baseline} and {target}")
    total_error = 0
    changed_pixels = 0
    for index in range(0, len(baseline_pixels), 3):
        red_error = abs(baseline_pixels[index] - target_pixels[index])
        green_error = abs(baseline_pixels[index + 1] - target_pixels[index + 1])
        blue_error = abs(baseline_pixels[index + 2] - target_pixels[index + 2])
        total_error += red_error + green_error + blue_error
        changed_pixels += max(red_error, green_error, blue_error) > PIXEL_ERROR_THRESHOLD
    mean_error = total_error / len(baseline_pixels)
    fraction = changed_pixels / (dimensions[0] * dimensions[1])
    return {
        "width": dimensions[0],
        "height": dimensions[1],
        "mean_channel_error": mean_error,
        "changed_pixel_fraction": fraction,
        "passed": mean_error <= MAX_MEAN_CHANNEL_ERROR and fraction <= MAX_CHANGED_PIXEL_FRACTION,
    }


def check_player(player, target):
    if target == "macos":
        executable = player / "Contents/MacOS" / read_bundle_info(player)["CFBundleExecutable"]
        with executable.open("rb") as binary:
            valid = binary.read(8) == struct.pack("<II", 0xFEEDFACF, 0x0100000C)
    elif target == "windows":
        executable = player
        with executable.open("rb") as binary:
            header = binary.read(64)
            valid = len(header) == 64 and header[:2] == b"MZ"
            if valid:
                binary.seek(struct.unpack_from("<I", header, 60)[0])
                valid = binary.read(6) == b"PE\0\0\x64\x86"
        valid = valid and player.with_name("UnityPlayer.dll").is_file()
    else:
        executable = player
        with executable.open("rb") as binary:
            header = binary.read(20)
        valid = header.startswith(b"\x7fELF\x02\x01\x01") and header[18:20] == b"\x3e\x00"
        valid = valid and player.with_name("UnityPlayer.so").is_file()
    if target != "macos":
        valid = valid and player.with_name(player.stem + "_Data").is_dir()
    if not valid:
        raise ValidationError(f"Incomplete {target} player or wrong architecture: {executable}")
    return executable


def run_fixture(editor, target, output, *, build_only, keep_player, reference, coreclr):
    profile = TARGETS[target]
    scripting_backend = "CoreCLR" if coreclr else "Mono"
    output.mkdir()
    summary = {"version": editor.version, "architecture": profile.architecture,
               "scripting_backend": scripting_backend,
               "graphics_api": profile.graphics, "status": "built", "passed": True}
    with tempfile.TemporaryDirectory(prefix="unity-engine-probe-") as temporary:
        scratch = Path(temporary)
        project = scratch / "project"
        project.mkdir()
        shutil.copytree(FIXTURE / "Assets", project / "Assets")
        packages = project / "Packages"
        packages.mkdir()
        shutil.copyfile(FIXTURE / "Packages/manifest.json", packages / "manifest.json")
        settings = project / "ProjectSettings"
        settings.mkdir()
        (settings / "ProjectVersion.txt").write_text(f"m_EditorVersion: {editor.version}\n")
        player_dir = scratch / "player"
        player_dir.mkdir()
        player = player_dir / profile.player_name
        env = os.environ.copy()
        env["UNITY_PROBE_PLAYER"] = str(player)
        env["UNITY_PROBE_TARGET"] = target
        env["UNITY_PROBE_CORECLR"] = "1" if coreclr else "0"
        run_process([
            editor.executable, "-batchmode", "-nographics", "-quit", "-projectPath", project,
            "-buildTarget", profile.unity_target,
            "-executeMethod", "EngineValidation.BuildProbe.Run", "-logFile", output / "editor.log",
        ], output / "editor-process.log", env, 1200)
        executable = check_player(player, target)
        if not build_only:
            run_process([
                executable, profile.graphics_flag, "-logFile", output / "player.log",
                "-probe-output", output,
            ], output / "player-process.log", env, 90)
            checks = read_report(output, editor.version, profile.graphics, scripting_backend)
            summary.update(status="ran", checks=sorted(checks))
        if keep_player:
            shutil.move(player_dir, output / "player")
    if reference:
        comparisons = {}
        for capture in CAPTURES:
            ppm = Path(capture).with_suffix(".ppm")
            comparisons[capture] = compare_frames(reference / ppm, output / ppm)
        summary["captures"] = comparisons
        summary["passed"] = all(item["passed"] for item in comparisons.values())
    return summary


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", type=Path, help=f"Unity 6.7 app or executable (default: Hub {TARGET_VERSION})")
    parser.add_argument("--platform", choices=(*TARGETS, "all"), help="player platform (default: host)")
    parser.add_argument("--build-only", action="store_true", help="build and check packaging without launching")
    parser.add_argument("--keep-player", action="store_true", help="retain the built player in the output directory")
    parser.add_argument("--coreclr", action="store_true", help="exercise Unity 6.7's experimental CoreCLR player")
    parser.add_argument("--reference", type=Path, help="compare pixels with an existing fixture capture directory")
    parser.add_argument("--output", type=Path, required=True,
                        help="new directory for local reports, captures and logs")
    args = parser.parse_args(argv)
    try:
        host = {"darwin": "macos", "linux": "linux", "win32": "windows"}.get(sys.platform)
        if host is None:
            raise ValidationError(f"Unsupported desktop host: {sys.platform}")
        selected = args.platform or host
        if not args.build_only:
            if selected != host:
                raise ValidationError("Use --build-only when cross-compiling")
            machine = platform.machine().lower()
            if host == "macos" and machine != "arm64":
                raise ValidationError("The macOS Metal player requires Apple Silicon")
            if host != "macos" and machine not in {"x86_64", "amd64"}:
                raise ValidationError(f"The {host} player requires an x86-64 desktop")
        elif args.reference:
            raise ValidationError("A reference comparison requires a player run")
        editor = find_editor(args.unity or default_editor(host))
        output = args.output.resolve()
        output.mkdir(parents=True)
        results = {}
        for target in (tuple(TARGETS) if selected == "all" else (selected,)):
            folder = output / target
            print(f"Building Unity {editor.version} for {target}. Logs: {folder}", flush=True)
            try:
                results[target] = run_fixture(editor, target, folder, build_only=args.build_only,
                                              keep_player=args.keep_player, reference=args.reference,
                                              coreclr=args.coreclr)
            except (OSError, ValueError, ValidationError, KeyError, plistlib.InvalidFileException) as error:
                results[target] = {"status": "failed", "error": str(error), "passed": False}
                print(f"{target}: {error}", file=sys.stderr)
        summary = {"unity_version": editor.version, "platforms": results,
                   "passed": all(item["passed"] for item in results.values())}
        report = output / "summary.json"
        report.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        print(f"{'Passed' if summary['passed'] else 'Failed'}: {report}")
        return 0 if summary["passed"] else 1
    except (OSError, ValueError, ValidationError) as error:
        print(f"Validation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
