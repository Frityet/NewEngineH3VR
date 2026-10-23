#!/usr/bin/env python3
"""Verify the frozen IEEE-mask repair; stage only with explicit --apply.

Dry-run writes nothing. Apply requires the current accepted native bundle proof,
and rejects any overlap with its captured source/managed inputs. Historical
manifests remain immutable; staging-verification.json is an additive receipt.
"""

from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import sys
import tempfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "script"))
from verify_native_bundles import verify, CONTENT_MARKER, COMPILER_RECEIPT, COMPILER_LOG
from verify_shader_proof_logs import inspect as inspect_log, POLICY_PATH


def require(ok, message):
    if not ok:
        raise ValueError(message)


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def inside(base, name):
    path = base / name
    require(
        not Path(name).is_absolute() and path.resolve().is_relative_to(base.resolve()),
        "Escaping path: " + name,
    )
    require(path == path.resolve(), "Symlink/noncanonical path: " + str(path))
    return path


def check(path, expected):
    require(path.is_file() and sha(path) == expected, "Hash mismatch/missing: " + str(path))


def read(path):
    return json.loads(path.read_text())


def disjoint(paths, receipt, project):
    captured = set()
    for field in ("sourceFiles", "managedInputs"):
        require(isinstance(receipt.get(field), list) and receipt[field], "Missing bundle " + field)
        for row in receipt[field]:
            p = Path(row["path"])
            captured.add((p if p.is_absolute() else project / p).resolve())
    overlap = sorted(str(p) for p in set(paths) & captured)
    require(not overlap, "Repair overlaps accepted bundle inputs: " + repr(overlap))
    return len(captured)


def validate(bundle_directory):
    plan_path = BASE / "staging-plan.json"
    plan = read(plan_path)
    parents = {str(plan_path.relative_to(ROOT)): sha(plan_path)}
    for name, expected in (
        ("shaders/ieee-mask-repair/candidate-manifest.json", plan["candidate_manifest_sha256"]),
        ("shaders/ieee-mask-repair/original/manifest.json", plan["original_manifest_sha256"]),
        (plan["baseline_manifest"], plan["baseline_manifest_sha256"]),
        (
            "shaders/semantic-v2-staging-verification.json",
            plan["baseline_staging_verification_sha256"],
        ),
    ):
        check(inside(ROOT, name), expected)
        parents[name] = expected
    native_path = ROOT / "ieee-mask-probe/output/verification.json"
    native = read(native_path)
    receipt_audit = read(ROOT / "ieee-mask-probe/output/receipt-hash-audit.json")
    require(
        receipt_audit
        == {"passed": True, "files": 240, "receiptSha256": sha(native_path), "bad": []},
        "Native receipt audit changed/failed",
    )
    expected_metrics = {
        "passed": True,
        "architecture": "arm64",
        "graphics": "Metal",
        "cases": 1548,
        "positiveDraws": 516,
        "positiveFailures": 0,
        "transportBitChanges": 0,
        "copyBitChanges": 0,
        "brokenRejected": 54,
        "broadRejected": 150,
    }
    require(
        all(native.get(k) == v for k, v in expected_metrics.items()),
        "Native proof scope/result mismatch",
    )
    require(len(native["hashes"]) == 240, "Expected all 240 native receipt fingerprints")
    for name, expected in native["hashes"].items():
        check(inside(ROOT, name), expected)
    parents[str(native_path.relative_to(ROOT))] = sha(native_path)
    parents["ieee-mask-probe/output/receipt-hash-audit.json"] = sha(
        ROOT / "ieee-mask-probe/output/receipt-hash-audit.json"
    )
    logs = []
    for name in ("ieee-mask-probe/output/build.log", "ieee-mask-probe/output/player.log"):
        require(name in native["hashes"], "Raw native log not bound")
        result = inspect_log(ROOT / name)
        require(result["passed"], "Native raw compiler/player log failed: " + name)
        logs.append({"path": name, "sha256": result["sha256"]})
    candidate = read(BASE / "candidate-manifest.json")
    baseline = read(inside(ROOT, plan["baseline_manifest"]))
    staging = read(ROOT / "shaders/semantic-v2-staging-verification.json")
    require(
        baseline.get("passed") is True and staging.get("passed") is True,
        "Historical baseline failed",
    )
    require(
        candidate["original_manifest_sha256"] == plan["original_manifest_sha256"],
        "Original parent differs",
    )
    changed = [r for r in candidate["files"] if r["before_sha256"] != r["after_sha256"]]
    require(
        len(changed) == 6 and changed == plan["files"],
        "Plan must contain exactly the six reviewed candidate deltas",
    )
    require(
        len({r["path"] for r in candidate["files"]}) == len(candidate["files"]) == 35,
        "Candidate file set changed",
    )
    baseline_hashes = dict(baseline["dependencies"])
    baseline_hashes.update({r["identity"]["exported_path"]: r["sha256"] for r in baseline["rows"]})
    candidate_names = {r["path"] for r in candidate["files"]}
    require(
        {
            str(p.relative_to(BASE / "candidate"))
            for p in (BASE / "candidate").rglob("*")
            if p.is_file()
        }
        == candidate_names,
        "Candidate tree changed",
    )
    for row in candidate["files"]:
        require(
            baseline_hashes.get(row["path"]) == row["before_sha256"],
            "Candidate before hash differs from frozen baseline",
        )
        candidate_path = inside(BASE / "candidate", row["path"])
        check(candidate_path, row["after_sha256"])
        require(
            native["hashes"].get(str(candidate_path.relative_to(ROOT))) == row["after_sha256"],
            "Candidate not in native receipt",
        )
    for row in changed:
        check(inside(BASE / "before", row["path"]), row["before_sha256"])
    for row in read(BASE / "original/manifest.json"):
        check(inside(ROOT, row["file"]), row["sha256"])
    require(
        {r["project"] for r in plan["projects"]} == {"unity", "unity-full"}
        and len(plan["projects"]) == 2,
        "Unexpected projects",
    )
    expected_shader_metas = {r["path"] + ".meta" for r in candidate["targets"]}
    writes, metas = [], {}
    for project in plan["projects"]:
        project_dir = ROOT / project["project"]
        require(
            {r["path"] for r in project["shader_metas"]} == expected_shader_metas
            and len(project["shader_metas"]) == 3,
            "Shader meta set differs",
        )
        for meta in project["shader_metas"]:
            path = inside(project_dir, meta["path"])
            check(path, meta["sha256"])
            require(
                re.search(r"^guid: " + meta["guid"] + "$", path.read_text(), re.M),
                "Shader GUID changed",
            )
            metas[str(path.relative_to(ROOT))] = meta["sha256"]
        for row in candidate["files"]:
            path = inside(project_dir, row["path"])
            current = sha(path)
            require(
                current in (row["before_sha256"], row["after_sha256"]),
                "Unexpected project source: " + str(path),
            )
            if row in changed:
                writes.append(
                    dict(
                        row,
                        project=project["project"],
                        target=str(path.relative_to(ROOT)),
                        observed_sha256=current,
                    )
                )
                meta_path = Path(str(path) + ".meta")
                if meta_path.exists():
                    metas[str(meta_path.relative_to(ROOT))] = sha(meta_path)
    require(len(writes) == 12, "Expected twelve project targets")
    bundle_error = None
    bundle_binding = {}
    try:
        receipt_path = bundle_directory / COMPILER_RECEIPT
        receipt = read(receipt_path)
        require(
            Path(receipt["buildLogPath"]).resolve() == ROOT / "build/native-bundles-build.log",
            "Not the current bundle build receipt",
        )
        count = disjoint([ROOT / w["target"] for w in writes], receipt, ROOT / "unity-full")
        verify(bundle_directory, ROOT / "build/native-bundle-plan.json", ROOT / "unity-full")
        bundle_binding = {
            "directory": str(bundle_directory.relative_to(ROOT)),
            "runId": receipt["runId"],
            "capturedInputs": count,
            "changedPathOverlap": [],
            "hashes": {
                name: sha(bundle_directory / name)
                for name in (CONTENT_MARKER, COMPILER_RECEIPT, COMPILER_LOG)
            },
        }
    except (OSError, KeyError, ValueError) as error:
        bundle_error = str(error)
    return {
        "schemaVersion": 1,
        "ready": bundle_error is None,
        "bundleBlocker": bundle_error,
        "parents": parents,
        "nativeReceiptFilesVerified": 240,
        "nativeLogs": logs,
        "compilerPolicySha256": sha(POLICY_PATH),
        "bundleProof": bundle_binding,
        "writes": writes,
        "preservedMetas": metas,
        "stageScriptSha256": sha(Path(__file__)),
    }


def apply(result, bundle_directory):
    require(
        result["ready"],
        "Accepted current bundle content proof required: " + str(result["bundleBlocker"]),
    )
    output = BASE / "staging-verification.json"
    if output.exists():
        prior = read(output)
        for key in (
            "parents",
            "nativeLogs",
            "compilerPolicySha256",
            "bundleProof",
            "preservedMetas",
            "stageScriptSha256",
        ):
            require(prior.get(key) == result[key], "Existing staging receipt differs: " + key)
        require(
            prior.get("passed") is True and prior.get("applied") is True,
            "Invalid prior staging receipt",
        )

        def intended(rows):
            return [{k: v for k, v in row.items() if k != "observed_sha256"} for row in rows]

        require(
            intended(prior.get("writes", [])) == intended(result["writes"]),
            "Prior staging target set differs",
        )
        for row in result["writes"]:
            check(ROOT / row["target"], row["after_sha256"])
        return prior
    # All validations finish before the first project mutation. Atomic per-file
    # replacement permits a safe rerun after interruption; exact-after is allowed.
    for row in result["writes"]:
        check(ROOT / row["target"], row["observed_sha256"])
    for name, expected in result["preservedMetas"].items():
        check(ROOT / name, expected)
    for name, expected in result["parents"].items():
        check(ROOT / name, expected)
    for row in result["writes"]:
        target = ROOT / row["target"]
        check(target, row["observed_sha256"])
        if row["observed_sha256"] == row["after_sha256"]:
            continue
        data = (BASE / "candidate" / row["path"]).read_bytes()
        require(
            hashlib.sha256(data).hexdigest() == row["after_sha256"],
            "Candidate changed before write",
        )
        fd, name = tempfile.mkstemp(prefix="." + target.name + ".", dir=target.parent)
        try:
            with os.fdopen(fd, "wb") as f:
                f.write(data)
            os.chmod(name, target.stat().st_mode & 0o777)
            os.replace(name, target)
        finally:
            Path(name).unlink(missing_ok=True)
    for row in result["writes"]:
        check(ROOT / row["target"], row["after_sha256"])
    for name, expected in result["preservedMetas"].items():
        check(ROOT / name, expected)
    for name, expected in result["parents"].items():
        check(ROOT / name, expected)
    # Do not waive the bundle gate after staging either.
    verify(bundle_directory, ROOT / "build/native-bundle-plan.json", ROOT / "unity-full")
    result.update(
        passed=True,
        applied=True,
        writtenFiles=sum(r["observed_sha256"] != r["after_sha256"] for r in result["writes"]),
    )
    with output.open("x") as f:
        json.dump(result, f, indent=2)
        f.write("\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument(
        "--bundle-directory", type=Path, default=ROOT / "unity-full/Assets/StreamingAssets"
    )
    args = parser.parse_args()
    directory = args.bundle_directory.resolve()
    require(
        directory in (ROOT / "build/StreamingAssets", ROOT / "unity-full/Assets/StreamingAssets"),
        "Only current bundle output/staging directories are accepted",
    )
    result = validate(directory)
    if args.apply:
        result = apply(result, directory)
    print(json.dumps(result, indent=2))
    return 0 if result["ready"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
