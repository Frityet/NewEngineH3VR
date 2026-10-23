#!/usr/bin/env python3
"""Install a verified runtime candidate into the local game and staging copies."""

import datetime
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "script"))
from audit_native_app import audit


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    out = ROOT / "build/refresh-rate"
    receipt = json.loads((out / "build.json").read_text())
    if not receipt.get("passed"):
        raise ValueError("A passing refresh runtime build is required")
    for name, expected in receipt["inputs"].items():
        if digest(Path(name)) != expected:
            raise ValueError("Candidate input changed; rebuild first: " + name)
    app = ROOT / "dist/NewEngineH3VR.app"
    processes = subprocess.check_output(["ps", "-axo", "comm="], text=True).splitlines()
    if str(app / "Contents/MacOS/NewEngineH3VRPlayer") in processes:
        raise ValueError("Quit NewEngineH3VR before replacing its runtime")
    candidate = out / "runtime/build/bin/libVirtualRealityOpenXR.dylib"
    expected = receipt["binaries"][candidate.name]
    if digest(candidate) != expected:
        raise ValueError("Runtime candidate hash changed")
    before = audit(app)
    if not before["passed"]:
        raise ValueError("Current app audit failed: " + repr(before["errors"]))
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup = out / ("install-" + stamp)
    backup.mkdir()
    (backup / "before-app-audit.json").write_text(json.dumps(before, indent=2) + "\n")
    provenance_path = ROOT / "native/build/runtime/provenance.json"
    shutil.copy2(provenance_path, backup / "base-runtime-provenance.json")
    destinations = [
        ROOT / "native/build/runtime/build/bin" / candidate.name,
        app / "Contents/Resources/Data/StreamingAssets/NewEngine/build/bin" / candidate.name,
    ]
    for project in ["unity", "unity-full"]:
        path = ROOT / project / "Assets/StreamingAssets/NewEngine/build/bin" / candidate.name
        if path.is_file():
            destinations.append(path)
    changes = []
    for index, destination in enumerate(destinations):
        old = backup / (str(index) + "-" + candidate.name)
        shutil.copy2(destination, old)
        changes.append(
            {
                "path": str(destination),
                "beforeSha256": digest(old),
                "afterSha256": expected,
                "backup": str(old),
            }
        )
    update = {
        "scope": "Refresh-aware OpenXR runtime only; gameplay assemblies and content bundles unchanged",
        "changes": changes,
        "buildReceiptSha256": digest(out / "build.json"),
        "installed": False,
    }
    update_path = backup / "update.json"
    update_path.write_text(json.dumps(update, indent=2) + "\n")
    try:
        for destination in destinations:
            shutil.copy2(candidate, destination)
        subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
        after = audit(app)
        if not after["passed"]:
            raise ValueError("Updated app audit failed: " + repr(after["errors"]))
        old_inventory = {r["path"]: r["sha256"] for r in before["binaries"]}
        changed = [
            r["path"] for r in after["binaries"] if old_inventory.get(r["path"]) != r["sha256"]
        ]
        # Re-sealing the app changes its launcher's embedded code signature.
        allowed = {destinations[1].relative_to(app).as_posix(), "Contents/MacOS/NewEngineH3VR"}
        if not set(changed) <= allowed:
            raise ValueError("Unexpected packaged binary changes: " + repr(changed))
        provenance = json.loads(provenance_path.read_text())
        provenance["pinned_binaries"][candidate.name] = expected
        provenance["local_refresh_extension"] = {
            "buildReceipt": str(out / "build.json"),
            "buildReceiptSha256": digest(out / "build.json"),
            "baseProvenance": str(backup / "base-runtime-provenance.json"),
        }
        provenance_path.write_text(json.dumps(provenance, indent=2) + "\n")
        (ROOT / "build/native-app-audit.json").write_text(json.dumps(after, indent=2) + "\n")
        update.update(
            installed=True,
            inventorySha256=after["inventorySha256"],
            signatureVerified=True,
            changedPackagedBinaries=changed,
        )
        update_path.write_text(json.dumps(update, indent=2) + "\n")
    except Exception:
        for change in changes:
            shutil.copy2(change["backup"], change["path"])
        shutil.copy2(backup / "base-runtime-provenance.json", provenance_path)
        subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
        restored = audit(app)
        (ROOT / "build/native-app-audit.json").write_text(json.dumps(restored, indent=2) + "\n")
        raise
    print("Installed and signed refresh-aware runtime; rollback receipt: " + str(update_path))


if __name__ == "__main__":
    main()
