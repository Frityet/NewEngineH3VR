#!/usr/bin/env python3
"""Launch a private Apple Silicon player against a verified MacVR package."""
import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_runtime(root):
    root = root.resolve(strict=True)
    receipt = json.loads((root / "receipt.json").read_text())
    if (receipt["schemaVersion"], receipt["architecture"], receipt["name"]) != (1, "arm64", "MacVR"):
        raise ValueError("Expected a schema 1 arm64 MacVR OpenXR package")
    manifest = root / "macvr.json"
    library = json.loads(manifest.read_text())["runtime"]["library_path"]
    if library != "libMacVROpenXR.dylib" and library != "./libMacVROpenXR.dylib":
        raise ValueError("Expected a relocatable MacVR runtime manifest")
    hashes = receipt["files"]
    required = ("macvr.json", "libMacVROpenXR.dylib", "libIndexCore.dylib", "libopenxr_loader.dylib")
    for name in required:
        path = root / name
        if path.is_symlink() or hashes.get(name) != digest(path):
            raise ValueError(f"MacVR package hash mismatch for {name}")
    return manifest, root / required[-1], receipt


def verify_player(app):
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    name = info["CFBundleExecutable"]
    if Path(name).name != name:
        raise ValueError("Invalid app executable name")
    program = app / "Contents/MacOS" / name
    arches = subprocess.check_output(["lipo", "-archs", program], text=True).split()
    if arches != ["arm64"]:
        raise ValueError(f"Expected a thin arm64 app executable, found {arches}")
    subprocess.run(["codesign", "--verify", "--deep", "--strict", app], check=True)
    stamp = app / "Contents/Resources/nativeh3vr-build.json"
    if stamp.is_file():
        receipt = json.loads(stamp.read_text())
        if (receipt["schemaVersion"], receipt["architecture"], receipt["buildTarget"],
                receipt["playerExecutable"]) != (1, "arm64", "StandaloneOSX", name):
            raise ValueError("Unsupported NativeH3VR app receipt")
        managed = app / "Contents/Resources/Data/Managed"
        for name, sha in receipt["managedAssemblies"].items():
            if Path(name).name != name or digest(managed / name) != sha:
                raise ValueError(f"Stock managed file failed receipt verification: {name}")
    return program, stamp.is_file()


@contextmanager
def private_state(root):
    root.mkdir(parents=True, exist_ok=True)
    with (root / ".nativeh3vr.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def run(app, runtime, state, synthetic, log, player_args, *, trace=None, profile=None, capture=None, timeout=None):
    app = app.resolve(strict=True)
    state = state.resolve()
    manifest, loader, receipt = verify_runtime(runtime)
    program, stock = verify_player(app)
    if not synthetic and not stock:
        raise ValueError("A headset launch requires a locally imported NativeH3VR app")
    if state.is_relative_to(app) or state.is_relative_to(manifest.parent):
        raise ValueError("Save state must be outside the signed app and runtime package")
    outputs = [p.resolve() for p in (log, trace, profile, capture) if p is not None]
    if len(set(outputs)) != len(outputs):
        raise ValueError("Each run output must have a distinct path")
    if any(p.exists() or p.is_relative_to(app) or p.is_relative_to(manifest.parent) for p in outputs):
        raise ValueError("Choose fresh output paths outside the signed app and runtime package")
    if any(arg.lower().startswith("-logfile") or arg in ("-nographics", "-force-glcore")
           for arg in player_args):
        raise ValueError("The launcher manages the player log and requires Metal")

    with private_state(state), tempfile.TemporaryDirectory(prefix="nh3vr-xr-") as endpoints:
        for path in outputs:
            path.parent.mkdir(parents=True, exist_ok=True)
        settings = state / "preferences"
        settings.mkdir(exist_ok=True)
        env = os.environ.copy()
        for key in ("XR_API_LAYER_PATH", "XR_ENABLE_API_LAYERS", "VR_OPENXR_FRAME_TRACE",
                    "NATIVEH3VR_UNITY_TRACE", "VR_OPENXR_CAPTURE", "VR_OPENXR_CAPTURE_EVERY"):
            env.pop(key, None)
        env.update({
            "XR_RUNTIME_JSON": str(manifest),
            "NATIVEH3VR_OPENXR_LOADER": str(loader),
            "NATIVEH3VR_STATE_DIR": str(state),
            "CFFIXED_USER_HOME": str(settings),
            "VR_OPENXR_SIMULATE": "1" if synthetic else "0",
            "VR_OPENXR_HEADLESS": "1" if synthetic else "0",
        })
        if synthetic:
            macvr = state / "macvr"
            macvr.mkdir(exist_ok=True)
            env["VR_STATE_DIR"] = str(macvr)
            env["VR_TRACKING_SOCKET"] = str(Path(endpoints) / "tracking.sock")
        if stock:
            env["SteamAppId"] = env["SteamGameId"] = "450540"
        if trace:
            env["VR_OPENXR_FRAME_TRACE"] = str(trace.resolve())
        if profile:
            env["NATIVEH3VR_UNITY_TRACE"] = str(profile.resolve())
        if capture:
            env["VR_OPENXR_CAPTURE"] = str(capture.resolve())
        print(f"MacVR {receipt['version']} revision {receipt['sourceRevision']} from {manifest}", flush=True)
        result = subprocess.run([program, "-force-metal", "-logFile", log.resolve(), *player_args],
                                cwd=state, env=env, timeout=timeout)
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--state-dir", type=Path, required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--synthetic", action="store_true")
    mode.add_argument("--headset", action="store_true")
    parser.add_argument("--log-file", type=Path, required=True)
    parser.add_argument("--trace-file", type=Path)
    parser.add_argument("--unity-profile-file", type=Path)
    parser.add_argument("--capture-file", type=Path)
    parser.add_argument("--timeout", type=float, help="Stop the child player after this many seconds")
    parser.add_argument("player_args", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.timeout is not None and args.timeout <= 0:
        parser.error("--timeout must be positive")
    extra = args.player_args[1:] if args.player_args[:1] == ["--"] else args.player_args
    try:
        return run(args.app, args.runtime, args.state_dir, args.synthetic, args.log_file, extra,
                   trace=args.trace_file, profile=args.unity_profile_file, capture=args.capture_file,
                   timeout=args.timeout)
    except (OSError, KeyError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"NativeH3VR launch failed: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
