#!/usr/bin/env python3
"""Migrate the recorded local package to NewEngineH3VR, retaining save/asset IDs."""

from pathlib import Path
import json
import plistlib
import shutil
import subprocess
from audit_native_app import audit, check_saved_audit, sha256, sign_packaged_binaries
from package_native import prepare_player

ROOT = Path(__file__).resolve().parents[1]
OLD = ROOT / "dist/NativeH3VR Experimental.app"
NEW = ROOT / "dist/NewEngineH3VR.app"
AUDIT = ROOT / "build/native-app-audit.json"
RECEIPT = ROOT / "build/new-engine-app-migration"
PREPARED = ROOT / "build/new-engine-runtime-path"
ADAPTER = Path("Contents/Resources/Data/Managed/NativeH3VR.SteamVR.Adapter.dll")
STREAMING = Path("Contents/Resources/Data/StreamingAssets")


def seal(app):
    sign_packaged_binaries(app)
    subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
    result = audit(app)
    if not result["passed"]:
        raise RuntimeError("Renamed package failed its audit: " + repr(result["errors"]))
    AUDIT.write_text(json.dumps(result, indent=2) + "\n")
    return result


def main():
    if not OLD.is_dir() or OLD.is_symlink() or NEW.exists() or RECEIPT.exists():
        raise ValueError(
            "Expected the original generated app, an absent destination and a new receipt"
        )
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True).splitlines()
    if any(
        line.startswith(str(app / "Contents/MacOS") + "/")
        for app in (OLD, NEW)
        for line in processes
    ):
        raise ValueError("Quit the generated player before renaming it")
    check_saved_audit(OLD, AUDIT)
    proof = json.loads((PREPARED / "runtime-path.json").read_text())
    candidate = PREPARED / ADAPTER.name
    if (
        sha256(OLD / ADAPTER) != proof["input_sha256"]
        or sha256(candidate) != proof["output_sha256"]
        or sha256(Path(proof["intent_source"])) != proof["intent_source_sha256"]
        or not proof["all_other_file_bytes_identical"]
        or not proof["raw_method_il_identical"]
        or not proof["metadata_tables_and_non_userstring_heaps_identical"]
        or not proof["assembly_contract"]["passed"]
    ):
        raise ValueError("Runtime-path candidate/proof changed or lacks its preservation checks")
    if not (OLD / STREAMING / "NativeRuntime").is_dir() or (OLD / STREAMING / "NewEngine").exists():
        raise ValueError("Ambiguous runtime directory layout")
    subprocess.run(["python3", str(ROOT / "platform/launch/build.py")], check=True)
    before = json.loads(AUDIT.read_text())
    RECEIPT.mkdir(parents=True)
    shutil.copy2(AUDIT, RECEIPT / "before-audit.json")
    shutil.copy2(PREPARED / "runtime-path.json", RECEIPT / "runtime-path.json")
    for relative in (
        ADAPTER,
        Path("Contents/Info.plist"),
        Path("Contents/MacOS/NativeH3VR"),
        Path("Contents/MacOS/NativeH3VRPlayer"),
    ):
        shutil.copy2(OLD / relative, RECEIPT / relative.name)
    content = STREAMING / "native-bundle-content-verification.json"
    content_hash = sha256(OLD / content)
    record = dict(
        scope="Public bundle/launcher/runtime directory rename; one adapter path literal",
        oldApp=str(OLD),
        app=str(NEW),
        runtimePath=proof,
        saveAndSerializedIdentities="Retained",
        contentProofSha256=content_hash,
        previousInventorySha256=before["inventorySha256"],
        passed=False,
    )
    check_saved_audit(OLD, AUDIT)
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True).splitlines()
    if any(
        line.startswith(str(app / "Contents/MacOS") + "/")
        for app in (OLD, NEW)
        for line in processes
    ):
        raise ValueError("Generated player started during migration preparation")
    try:
        OLD.rename(NEW)
        (NEW / STREAMING / "NativeRuntime").rename(NEW / STREAMING / "NewEngine")
        shutil.copy2(candidate, NEW / ADAPTER)
        if sha256(NEW / ADAPTER) != proof["output_sha256"]:
            raise RuntimeError("Installed runtime-path adapter differs from its proof")
        plist = NEW / "Contents/Info.plist"
        info = plistlib.loads(plist.read_bytes())
        prepare_player(NEW / "Contents/MacOS", info["CFBundleExecutable"])
        shutil.copy2(ROOT / "build/launch/NewEngineH3VR", NEW / "Contents/MacOS/NewEngineH3VR")
        for key in ("CFBundleExecutable", "CFBundleName", "CFBundleDisplayName"):
            info[key] = "NewEngineH3VR"
        plist.write_bytes(plistlib.dumps(info))
        if sha256(NEW / content) != content_hash:
            raise RuntimeError("Content receipt changed during name migration")
        result = seal(NEW)
        record.update(
            passed=True,
            inventorySha256=result["inventorySha256"],
            nativeCount=result["nativeCount"],
            managedCount=result["managedCount"],
        )
    except BaseException:
        if NEW.exists():
            for relative in (
                ADAPTER,
                Path("Contents/Info.plist"),
                Path("Contents/MacOS/NativeH3VR"),
                Path("Contents/MacOS/NativeH3VRPlayer"),
            ):
                shutil.copy2(RECEIPT / relative.name, NEW / relative)
            for name in ("NewEngineH3VR", "NewEngineH3VRPlayer"):
                (NEW / "Contents/MacOS" / name).unlink(missing_ok=True)
            if (NEW / STREAMING / "NewEngine").exists():
                (NEW / STREAMING / "NewEngine").rename(NEW / STREAMING / "NativeRuntime")
            NEW.rename(OLD)
            seal(OLD)
        record["rolledBack"] = True
        raise
    finally:
        (RECEIPT / "migration.json").write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps(record, indent=2))


if __name__ == "__main__":
    main()
