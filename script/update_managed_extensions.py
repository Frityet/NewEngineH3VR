#!/usr/bin/env python3
"""Install a recorded app-only update of this port's non-serialized extensions.

The Unity asset build and its managed-input proof remain a frozen baseline.
This update gets its own ABI, source, signature and runtime-test evidence.
"""

from pathlib import Path
import argparse
import json
import shutil
import subprocess
from audit_native_app import audit, sha256

ROOT = Path(__file__).resolve().parents[1]
NAMES = ("NativeH3VR.XR.Managed.dll", "NativeH3VR.GameplayProbe.dll")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidates", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()
    app = ROOT / "dist/NewEngineH3VR.app"
    managed = app / "Contents/Resources/Data/Managed"
    receipt = args.receipt.resolve()
    if not receipt.is_relative_to(ROOT / "build") or receipt.exists():
        raise SystemExit("Use a new receipt directory inside this project build directory")
    checker = (
        ROOT / "platform/managed-extension-check/bin/Release/net10.0/ManagedExtensionCheck.dll"
    )
    if not checker.is_file():
        raise SystemExit("Build platform/managed-extension-check in Release first")
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True).splitlines()
    if any(line.startswith(str(app / "Contents/MacOS") + "/") for line in processes):
        raise SystemExit("Quit this generated app before updating its managed extensions")
    saved_path = ROOT / "build/native-app-audit.json"
    saved = json.loads(saved_path.read_text())
    if saved.get("passed") is not True or Path(saved["app"]).resolve() != app:
        raise SystemExit("A successful app audit is required before an extension update")
    # Refuse to overlay an already changed or unrecorded binary inventory.
    for record in saved["binaries"] + saved["bindings"]:
        if sha256(app / record["path"]) != record["sha256"]:
            raise SystemExit("App changed since its saved audit: " + record["path"])
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    changes = []
    for name in NAMES:
        old, candidate = managed / name, args.candidates.resolve() / name
        if old.is_symlink() or candidate.is_symlink():
            raise SystemExit("Refusing symlink assembly inputs")
        check = subprocess.run(
            ["dotnet", str(checker), str(old), str(candidate)],
            capture_output=True,
            text=True,
            check=True,
        )
        contract = json.loads(check.stdout)
        if contract.get("passed") is not True:
            raise SystemExit("Extension contract failed: " + name)
        changes.append(
            {
                "assembly": name,
                "beforeSha256": sha256(old),
                "afterSha256": sha256(candidate),
                "contract": contract,
            }
        )
    receipt.mkdir(parents=True)
    shutil.copy2(saved_path, receipt / "before-native-app-audit.json")
    for name in NAMES:
        shutil.copy2(managed / name, receipt / name)
    source_files = [
        ROOT / "native/managed/NativeH3VRXR.cs",
        ROOT / "native/managed/NativePostProcessStereo.cs",
        ROOT / "native/managed/NativeScopeActivation.cs",
        ROOT / "native/build_managed.py",
        *sorted((ROOT / "platform/gameplay-probe").glob("*.cs")),
        ROOT / "platform/gameplay-probe/build.py",
    ]
    proof = app / "Contents/Resources/Data/StreamingAssets/native-bundle-content-verification.json"
    report = {
        "scope": "App-only managed extension update over unchanged content bundles",
        "contentProofLimitation": "Runtime extension assemblies differ from the managedInputs of the frozen Unity asset build; that proof is not a new managed-runtime validation.",
        "app": str(app),
        "changes": changes,
        "contentProof": {"path": str(proof), "sha256": sha256(proof)},
        "sourceSha256": {str(p.relative_to(ROOT)): sha256(p) for p in source_files},
        "rollback": "Restore the two backed-up DLLs only if current hashes match afterSha256, then re-sign the enclosing app and regenerate its audit.",
        "runtimeValidation": "pending",
    }
    (receipt / "update.json").write_text(json.dumps(report, indent=2) + "\n")
    try:
        for name in NAMES:
            shutil.copy2(args.candidates / name, managed / name)
        # Nested native code is unchanged; only reseal the enclosing app.
        subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
        result = audit(app)
        if not result["passed"]:
            raise RuntimeError("Updated application audit failed: " + repr(result["errors"]))
        before = {r["path"]: r["sha256"] for r in saved["binaries"]}
        after = {r["path"]: r["sha256"] for r in result["binaries"]}
        allowed = {"Contents/Resources/Data/Managed/" + n for n in NAMES}
        # Resealing may change the native launcher signature, but never its dependencies.
        allowed.add("Contents/MacOS/NewEngineH3VR")
        unexpected = {
            p for p in before.keys() | after.keys() if before.get(p) != after.get(p)
        } - allowed
        if unexpected or sha256(proof) != report["contentProof"]["sha256"]:
            raise RuntimeError("Unexpected app/content mutation: " + repr(sorted(unexpected)))
        result["managedExtensionUpdate"] = str(receipt / "update.json")
        saved_path.write_text(json.dumps(result, indent=2) + "\n")
        report["auditInventorySha256"] = result["inventorySha256"]
        report["signatureVerified"] = True
        (receipt / "update.json").write_text(json.dumps(report, indent=2) + "\n")
        print(
            json.dumps(
                {
                    "receipt": str(receipt),
                    "passed": True,
                    "nativeCount": result["nativeCount"],
                    "managedCount": result["managedCount"],
                }
            )
        )
    except BaseException:
        for name in NAMES:
            shutil.copy2(receipt / name, managed / name)
        subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
        rollback = audit(app)
        saved_path.write_text(json.dumps(rollback, indent=2) + "\n")
        report["rolledBack"] = True
        (receipt / "update.json").write_text(json.dumps(report, indent=2) + "\n")
        raise


if __name__ == "__main__":
    main()
