#!/usr/bin/env python3
"""Install a reversible Unity 2021 scope render-state correction in the port.

The frozen Unity bundle-build proof remains unchanged and describes the input
bundle. A separate overlay receipt binds the two corrected depth-clip values,
the preserved object/program bytes, and the actual packaged output.
"""

from pathlib import Path
import argparse
import json
import os
import shutil
import subprocess

from audit_native_app import audit, check_saved_audit, sha256

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = "assets_resources_objectids_weaponry_ar15"
MARKER = "new-engine-shader-state-overlay.json"
BASE_PROOF = "native-bundle-content-verification.json"
SOURCE = (
    ROOT
    / "audit-assets/all-shader-inputs/010-0-StreamingAssets_assets_resources_objectids_weaponry_ar15.assets"
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def require_stopped(app):
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True).splitlines()
    require(
        not any(line.startswith(str(app / "Contents/MacOS") + "/") for line in processes),
        "Quit this generated app before changing its render-state bundle",
    )


def atomic_copy(source, destination):
    temporary = destination.with_name(destination.name + ".new-engine-update")
    require(not temporary.exists(), "An unfinished update already exists: " + str(temporary))
    try:
        shutil.copy2(source, temporary)
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            temporary.unlink()


def validate_base_record(content_proof, bundle):
    require(
        content_proof.get("verified") is True and content_proof.get("schemaVersion") == 2,
        "A verified frozen Unity content baseline is required",
    )
    require(
        str(content_proof.get("unityVersion", "")).startswith("2021."),
        "This render-state correction is scoped to the Unity 2021 port",
    )
    records = [r for r in content_proof.get("outputs", []) if r.get("path") == BUNDLE]
    require(len(records) == 1, "Missing/ambiguous original bundle fingerprint")
    require(
        records[0].get("bytes") == bundle.stat().st_size
        and records[0].get("sha256") == sha256(bundle),
        "The bundle differs from its verified Unity build baseline",
    )
    return records[0]


def main():
    from patch_scope_depth_clip import prepare_bundle, verify_candidate

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, default=ROOT / "dist/NewEngineH3VR.app")
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument(
        "--before-package",
        action="store_true",
        help="Fresh build only: packaging will sign and audit the result next",
    )
    args = parser.parse_args()
    app, receipt = args.app.resolve(), args.receipt.resolve()
    require(
        app.is_relative_to(ROOT / "dist") and app.is_dir(), "Use the generated app inside dist/"
    )
    require(
        receipt.is_relative_to(ROOT / "build/new-engine-depth-clip") and not receipt.exists(),
        "Use a fresh receipt directory inside build/new-engine-depth-clip/",
    )
    require_stopped(app)
    assets = app / "Contents/Resources/Data/StreamingAssets"
    target, marker, content_path = assets / BUNDLE, assets / MARKER, assets / BASE_PROOF
    require(
        assets.resolve().is_relative_to(app)
        and target.resolve().is_relative_to(app)
        and content_path.resolve().is_relative_to(app),
        "Asset paths escape the generated app",
    )
    require(
        not marker.exists() and not marker.is_symlink(),
        "This app already has a shader-state overlay; preserve its recorded baseline",
    )
    require(target.is_file() and not target.is_symlink(), "Missing or symlink bundle input")
    saved_path = ROOT / "build/native-app-audit.json"
    saved = None
    if not args.before_package:
        check_saved_audit(app, saved_path)
        saved = json.loads(saved_path.read_text())
    content = json.loads(content_path.read_text())
    baseline = validate_base_record(content, target)
    content_hash = sha256(content_path)
    receipt.mkdir(parents=True)
    before = receipt / "before" / BUNDLE
    before.parent.mkdir()
    shutil.copy2(target, before)
    validate_base_record(content, before)
    if saved is not None:
        shutil.copy2(saved_path, receipt / "before-native-app-audit.json")
    candidate_dir = receipt / "candidate"
    prepare_bundle(before, SOURCE, candidate_dir)
    candidate = candidate_dir / BUNDLE
    proof_path = candidate_dir / "depth-clip-proof.json"
    verification = verify_candidate(before, candidate, proof_path)
    require(verification.get("passed") is True, "Independent shader-state verification failed")
    require(
        verification.get("inputSha256") == baseline["sha256"]
        and verification.get("candidateSha256") == sha256(candidate),
        "Independent verification does not bind the accepted baseline and candidate",
    )
    (receipt / "independent-verification.json").write_text(
        json.dumps(verification, indent=2) + "\n"
    )
    proof = json.loads(proof_path.read_text())
    report = {
        "schemaVersion": 1,
        "kind": "newengine-unity2021-scope-depth-clip",
        "scope": "Only the EC79A2 PIP shader FORWARDBASE and SHADOWCASTER depth-clipping defaults",
        "bundle": BUNDLE,
        "before": baseline,
        "after": {"path": BUNDLE, "bytes": candidate.stat().st_size, "sha256": sha256(candidate)},
        "baseContentProof": {"path": BASE_PROOF, "sha256": content_hash},
        "proof": proof,
        "proofEncoding": "json-indent-2-utf8-lf",
        "proofSha256": sha256(proof_path),
        "sourceSha256": {
            str(p.relative_to(ROOT)): sha256(p)
            for p in (Path(__file__).resolve(), ROOT / "script/patch_scope_depth_clip.py")
        },
        "receipt": str(receipt),
        "rollbackBundle": str(before),
        "limitations": [
            "The frozen Unity content proof describes the original bundle, not this corrected output.",
            "The overlay is independently checked at serialized-object/program-byte level; actual rendering is validated separately.",
            "Adjacent Unity manifest and AssetBundleManifest hashes remain build-baseline metadata; overlay SHA256 binds the delivered bytes.",
        ],
    }
    receipt_manifest = receipt / MARKER
    receipt_manifest.write_text(json.dumps(report, indent=2) + "\n")
    # Preparation parses a large bundle. Refuse to overwrite work or a game
    # launch that appeared while that read-only verification was running.
    require_stopped(app)
    require(
        not marker.exists() and not marker.is_symlink(), "An overlay appeared during preparation"
    )
    require(
        target.resolve().is_relative_to(app) and not target.is_symlink(),
        "Bundle path changed during preparation",
    )
    validate_base_record(content, target)
    validate_base_record(content, before)
    require(
        sha256(content_path) == content_hash, "Frozen content baseline changed during preparation"
    )
    if not args.before_package:
        check_saved_audit(app, saved_path)
        require(
            json.loads(saved_path.read_text()) == saved, "Saved audit changed during preparation"
        )
    replaced = False
    try:
        atomic_copy(candidate, target)
        replaced = True
        atomic_copy(receipt_manifest, marker)
        require(sha256(content_path) == content_hash, "Frozen content proof changed unexpectedly")
        if not args.before_package:
            subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
            result = audit(app)
            require(result["passed"], "Updated app audit failed: " + repr(result["errors"]))
            old = {r["path"]: r["sha256"] for r in saved["binaries"]}
            new = {r["path"]: r["sha256"] for r in result["binaries"]}
            changed = {p for p in old.keys() | new.keys() if old.get(p) != new.get(p)}
            require(
                changed <= {"Contents/MacOS/NewEngineH3VR"},
                "Unexpected binary mutation: " + repr(sorted(changed)),
            )
            result["shaderStateOverlay"] = str(receipt_manifest)
            saved_path.write_text(json.dumps(result, indent=2) + "\n")
        print(
            json.dumps(
                {
                    "passed": True,
                    "receipt": str(receipt),
                    "bundle": BUNDLE,
                    "beforeSha256": baseline["sha256"],
                    "afterSha256": report["after"]["sha256"],
                    "signedAndAudited": not args.before_package,
                }
            )
        )
    except BaseException as original_error:
        recovery = {
            "rolledBack": False,
            "transactionReplacedBundle": replaced,
            "originalError": repr(original_error),
            "rollbackErrors": [],
        }
        try:
            if replaced:
                atomic_copy(before, target)
                validate_base_record(content, target)
                if marker.exists():
                    marker.unlink()
                require(
                    not marker.exists() and not marker.is_symlink(),
                    "Overlay marker was not removed",
                )
                require(
                    sha256(content_path) == content_hash,
                    "Frozen content proof changed during rollback",
                )
                if not args.before_package:
                    subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
                    rollback = audit(app)
                    saved_path.write_text(json.dumps(rollback, indent=2) + "\n")
                    require(
                        rollback["passed"], "Rollback audit failed: " + repr(rollback["errors"])
                    )
                    old = {r["path"]: r["sha256"] for r in saved["binaries"]}
                    restored = {r["path"]: r["sha256"] for r in rollback["binaries"]}
                    changed = {
                        p for p in old.keys() | restored.keys() if old.get(p) != restored.get(p)
                    }
                    require(
                        changed <= {"Contents/MacOS/NewEngineH3VR"},
                        "Unrelated binary change persists after rollback",
                    )
                recovery["rolledBack"] = True
        except BaseException as rollback_error:
            recovery["rollbackErrors"].append(repr(rollback_error))
        finally:
            (receipt / "rolled-back.json").write_text(json.dumps(recovery, indent=2) + "\n")
        if recovery["rollbackErrors"]:
            raise RuntimeError(
                "Shader update failed; rollback is incomplete: " + repr(recovery["rollbackErrors"])
            ) from original_error
        raise


if __name__ == "__main__":
    main()
