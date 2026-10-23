#!/usr/bin/env python3
"""Stage an ABI-compatible native-only rebuild without rebuilding Unity assets."""

from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
from native_plugin_contract import HEADERS, check_bridge, exported_symbols

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--receipt",
        type=Path,
        required=True,
        help="New output directory for the update and small rollback files",
    )
    args = parser.parse_args()
    receipt_dir = args.receipt.resolve()
    if not receipt_dir.is_relative_to(ROOT / "build") or receipt_dir.exists():
        raise SystemExit("Use a new receipt directory inside this project build directory")
    composed = ROOT / "build/composed/final"
    provenance_path = composed / "vr-provenance.json"
    provenance = json.loads(provenance_path.read_text())
    # Rebuilding the native plugins must not silently accept managed changes.
    for name in ("NativeH3VR.XR.Managed.dll", "NativeH3VR.SteamVR.Adapter.dll"):
        if digest(composed / name) != provenance["output_sha256"][name]:
            raise SystemExit("Composed managed bridge changed: " + name)
    for name, expected in provenance["source_sha256"].items():
        if digest(ROOT / "native" / name) != expected:
            raise SystemExit("Managed bridge source changed; full composition required: " + name)
    build_sources = ROOT / "native/build/source-sha256.json"
    for name, expected in json.loads(build_sources.read_text()).items():
        if digest(Path(name)) != expected:
            raise SystemExit("Native source changed since build: " + name)
    jobs = []
    for name in HEADERS:
        source, old = ROOT / "native/build" / name, composed / name
        if digest(old) != provenance["output_sha256"][name]:
            raise SystemExit("Composed native bridge differs from provenance: " + name)
        if not exported_symbols(old).issubset(exported_symbols(source)):
            raise SystemExit("Native rebuild removed an existing export: " + name)
        check_bridge(source)
        if subprocess.check_output(["lipo", "-archs", str(source)], text=True).strip() != "arm64":
            raise SystemExit("Native rebuild is not ARM64: " + name)
        subprocess.run(["codesign", "--verify", "--strict", str(source)], check=True)
        destinations = [old, *sorted((ROOT / "unity-full/Assets/Plugins").rglob(name))]
        if len(destinations) < 2:
            raise SystemExit("No staged Unity native plugin: " + name)
        for destination in destinations:
            if destination.is_symlink():
                raise SystemExit("Refusing plugin symlink: " + str(destination))
        jobs.append((name, source, destinations))
    receipt_dir.mkdir(parents=True)
    shutil.copy2(provenance_path, receipt_dir / "previous-vr-provenance.json")
    shutil.copy2(build_sources, receipt_dir / "native-source-sha256.json")
    changes = []
    for name, source, destinations in jobs:
        shutil.copy2(composed / name, receipt_dir / name)
        expected = digest(source)
        for destination in destinations:
            before = digest(destination)
            shutil.copy2(source, destination)
            changes.append(
                {
                    "path": str(destination.relative_to(ROOT)),
                    "beforeSha256": before,
                    "afterSha256": expected,
                }
            )
        provenance["output_sha256"][name] = expected
    provenance["native_only_update"] = str(receipt_dir.relative_to(ROOT) / "update.json")
    provenance_path.write_text(json.dumps(provenance, indent=2) + "\n")
    report = {
        "scope": "ABI-compatible native plugin update; managed code and Unity content unchanged",
        "managedSourceHashesVerified": True,
        "existingExportsPreserved": True,
        "nativeSourceHashes": str(receipt_dir / "native-source-sha256.json"),
        "copies": changes,
    }
    (receipt_dir / "update.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
