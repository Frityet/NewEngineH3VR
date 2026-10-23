"""Static ABI and copy-identity checks for the project's native VR bridges."""

from pathlib import Path
import hashlib
import json
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
HEADERS = {
    "libNativeH3VRXR.dylib": ROOT / "native/include/nativeh3vr_xr.h",
    "libNativeH3VRTracking.dylib": ROOT / "native/include/nativeh3vr_tracking.h",
}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def exported_symbols(path):
    output = subprocess.check_output(["/usr/bin/nm", "-gU", str(path)], text=True)
    return {line.split()[-1].removeprefix("_") for line in output.splitlines() if line.split()}


def check_bridge(path):
    header = HEADERS[path.name]
    required = set(re.findall(r"^NH3VR_API\s+[^;]+?\b(nh3vr_\w+)\s*\(", header.read_text(), re.M))
    if not required:
        raise ValueError("Empty native bridge ABI: " + str(header))
    missing = required - exported_symbols(path)
    if missing:
        raise ValueError(f"{path}: missing public native bridge exports: {sorted(missing)}")
    return {
        "header": str(header),
        "headerSha256": digest(header),
        "requiredExports": sorted(required),
        "sha256": digest(path),
    }


def check_plugin_copies(app):
    """Reject search-order-dependent plugin selection; inspect every copy."""
    groups, checks = {}, []
    for path in sorted((app / "Contents/PlugIns").rglob("*.dylib")):
        if not path.resolve().is_relative_to(app.resolve()):
            raise ValueError("Plugin symlink escapes app: " + str(path))
        groups.setdefault(path.name, []).append(path)
        if path.name in HEADERS:
            checks.append({"path": path.relative_to(app).as_posix(), **check_bridge(path)})
    for name, paths in groups.items():
        if len({digest(path) for path in paths}) != 1:
            raise ValueError(
                "Conflicting packaged plugin copies: "
                + name
                + ": "
                + ", ".join(str(path.relative_to(app)) for path in paths)
            )
    managed = app / "Contents/Resources/Data/Managed"
    for assembly, library in [
        ("NativeH3VR.XR.Managed.dll", "libNativeH3VRXR.dylib"),
        ("NativeH3VR.SteamVR.Adapter.dll", "libNativeH3VRTracking.dylib"),
    ]:
        if (managed / assembly).exists() and library not in groups:
            raise ValueError("Managed native bridge library missing: " + library)
    return checks


def refresh_bridges(app, root=ROOT):
    """Refresh every existing copy, including obsolete roots retained by Unity.

    Use the same native outputs bound into the managed composition. Do not
    silently install a different native build just because it is newer.
    """
    receipt_path = root / "build/composed/final/vr-provenance.json"
    receipt = json.loads(receipt_path.read_text())
    jobs, changes = [], []
    for name in ("NativeH3VR.XR.Managed.dll", "NativeH3VR.SteamVR.Adapter.dll"):
        managed = app / "Contents/Resources/Data/Managed" / name
        if digest(managed) != receipt["output_sha256"][name]:
            raise ValueError("Packaged managed bridge differs from composed provenance: " + name)
    for name in HEADERS:
        source = root / "native/build" / name
        expected = receipt["output_sha256"][name]
        if digest(source) != expected:
            raise ValueError("Native bridge differs from composed provenance: " + name)
        check_bridge(source)
        if (
            subprocess.check_output(["/usr/bin/lipo", "-archs", str(source)], text=True).strip()
            != "arm64"
        ):
            raise ValueError("Native bridge is not ARM64-only: " + name)
        destinations = sorted((app / "Contents/PlugIns").rglob(name))
        if not destinations:
            raise ValueError("Unity player did not include native bridge: " + name)
        for destination in destinations:
            if destination.is_symlink() or not destination.resolve().is_relative_to(app.resolve()):
                raise ValueError("Unsafe native bridge destination: " + str(destination))
            jobs.append((source, destination, expected))
    # Validate all sources and destinations before modifying the application.
    for source, destination, expected in jobs:
        before = digest(destination)
        if before != expected:
            shutil.copy2(source, destination)
        if digest(destination) != expected:
            raise ValueError("Native bridge copy failed: " + str(destination))
        changes.append(
            {
                "path": destination.relative_to(app).as_posix(),
                "source": str(source),
                "beforeSha256": before,
                "afterSha256": expected,
            }
        )
    return {
        "provenance": str(receipt_path),
        "provenanceSha256": digest(receipt_path),
        "copies": changes,
    }
