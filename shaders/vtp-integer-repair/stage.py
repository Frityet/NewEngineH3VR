#!/usr/bin/env python3
"""Verify the frozen VTP integer repair; stage only with explicit --apply.

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
        ("shaders/vtp-integer-repair/candidate-manifest.json", plan["candidate_manifest_sha256"]),
        ("shaders/vtp-integer-repair/original/manifest.json", plan["original_manifest_sha256"]),
        (
            "shaders/vtp-integer-repair/original-to-candidate-map.json",
            plan["original_to_candidate_map_sha256"],
        ),
        (plan["baseline_manifest"], plan["baseline_manifest_sha256"]),
        (
            "shaders/semantic-v2-staging-verification.json",
            plan["baseline_staging_verification_sha256"],
        ),
    ):
        check(inside(ROOT, name), expected)
        parents[name] = expected
    proof_scopes = {
        "vtp-integer-probe/output/verification.json": {
            "count": 420,
            "positiveCases": 364,
            "negativeDetected": 114,
            "originalProgram": 114,
        },
        "vtp-family-probe/output/verification.json": {
            "count": 1364,
            "shaderObjects": 2,
            "draws": 376,
            "retentionCallbacks": 40,
            "retentionMissing": 0,
        },
    }
    require(set(plan["proofs"]) == set(proof_scopes), "Native proof set changed")
    native_proofs, logs = {}, []
    for name, scope in proof_scopes.items():
        path = inside(ROOT, name)
        check(path, plan["proofs"][name]["sha256"])
        parents[name] = sha(path)
        native = read(path)
        require(
            native.get("passed") is True
            and native.get("architecture") == "arm64"
            and native.get("graphics") == "Metal",
            "Native proof failed/platform differs",
        )
        require(
            all(native.get(k) == v for k, v in scope.items() if k != "count"),
            "Native proof scope changed",
        )
        require(
            len(native["hashes"]) == scope["count"] == plan["proofs"][name]["verified_hashes"],
            "Native fingerprint count changed",
        )
        for filename, expected in native["hashes"].items():
            check(inside(ROOT, filename), expected)
        native_proofs[name] = native
        for filename in (
            str(Path(name).parent / "build.log"),
            str(Path(name).parent / "player.log"),
        ):
            require(filename in native["hashes"], "Raw native log not bound")
            result = inspect_log(ROOT / filename)
            require(result["passed"], "Native raw log rejected: " + filename)
            logs.append({"path": filename, "sha256": result["sha256"]})
    candidate = read(BASE / "candidate-manifest.json")
    baseline = read(inside(ROOT, plan["baseline_manifest"]))
    staging = read(ROOT / "shaders/semantic-v2-staging-verification.json")
    require(
        baseline.get("passed") is True and staging.get("passed") is True,
        "Historical baseline failed",
    )
    mapping = read(BASE / "original-to-candidate-map.json")
    require(
        mapping.get("passed") is True
        and mapping["candidate_manifest_sha256"] == plan["candidate_manifest_sha256"]
        and mapping["original_manifest_sha256"] == plan["original_manifest_sha256"],
        "Original candidate mapping differs",
    )
    changed = [r for r in candidate["files"] if r["before_sha256"] != r["after_sha256"]]
    require(
        len(changed) == 374 and changed == plan["files"],
        "Plan must contain exactly the 374 reviewed include deltas",
    )
    require(
        len({r["path"] for r in candidate["files"]}) == len(candidate["files"]) == 379,
        "Candidate file set changed",
    )
    unchanged = [r for r in candidate["files"] if r["before_sha256"] == r["after_sha256"]]
    require(
        unchanged == plan["unchanged_files"] and len(unchanged) == 5, "Unchanged source set differs"
    )
    require(
        all(
            r["path"].startswith("Assets/NativeShaders/SemanticV2Variants/")
            and r["path"].endswith(".cginc")
            for r in changed
        ),
        "Only existing include deltas allowed",
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
        for native in native_proofs.values():
            require(
                native["hashes"].get(str(candidate_path.relative_to(ROOT))) == row["after_sha256"],
                "Candidate not in both native receipts",
            )
    for row in changed:
        check(inside(BASE / "before", row["path"]), row["before_sha256"])
    for row in read(BASE / "original/manifest.json")["programs"]:
        path = inside(ROOT, row["dxbc"])
        check(path, row["sha256"])
        check(path.with_suffix(".asm"), row["asm_sha256"])
    require(
        {r["project"] for r in plan["projects"]} == {"unity", "unity-full"}
        and len(plan["projects"]) == 2,
        "Unexpected projects",
    )
    expected_shader_metas = {candidate["source"] + ".meta"}
    expected_include_metas = {
        r["path"] + ".meta" for r in candidate["files"] if r["path"].endswith(".cginc")
    }
    writes, metas, absent_metas, unchanged_targets = [], {}, [], {}
    for project in plan["projects"]:
        project_dir = ROOT / project["project"]
        require(
            {r["path"] for r in project["shader_metas"]} == expected_shader_metas
            and len(project["shader_metas"]) == 1,
            "Shader meta set differs",
        )
        include_names = {r["path"] for r in project["include_metas"]}
        absent_names = set(project["absent_include_metas"])
        require(
            len(include_names) == len(project["include_metas"])
            and len(absent_names) == len(project["absent_include_metas"])
            and not (include_names & absent_names)
            and include_names | absent_names == expected_include_metas,
            "Include metadata coverage differs",
        )
        for name in sorted(absent_names):
            path = inside(project_dir, name)
            require(not path.exists(), "Previously absent include meta appeared: " + str(path))
            absent_metas.append(str(path.relative_to(ROOT)))
        for meta in project["shader_metas"] + project["include_metas"]:
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
            else:
                unchanged_targets[str(path.relative_to(ROOT))] = row["before_sha256"]
    require(len(writes) == 748, "Expected 748 project include targets")
    family_paths = [
        ROOT / project["project"] / row["path"]
        for project in plan["projects"]
        for row in candidate["files"]
    ]
    for archived in plan["archived_bundle_receipts"]:
        path = inside(ROOT, archived["path"])
        check(path, archived["sha256"])
        receipt = read(path)
        require(
            receipt["runId"] == archived["run_id"] and receipt["clean"] is True,
            "Archived compiler receipt identity differs",
        )
        disjoint(family_paths, receipt, ROOT / "unity-full")
        parents[archived["path"]] = archived["sha256"]
    bundle_error = None
    bundle_binding = {}
    try:
        receipt_path = bundle_directory / COMPILER_RECEIPT
        receipt = read(receipt_path)
        require(
            Path(receipt["buildLogPath"]).resolve() == ROOT / "build/native-bundles-build.log",
            "Not the current bundle build receipt",
        )
        count = disjoint(family_paths, receipt, ROOT / "unity-full")
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
        "nativeReceiptFilesVerified": {"arithmetic": 420, "family": 1364},
        "nativeLogs": logs,
        "compilerPolicySha256": sha(POLICY_PATH),
        "bundleProof": bundle_binding,
        "writes": writes,
        "preservedMetas": metas,
        "absentMetas": absent_metas,
        "unchangedTargets": unchanged_targets,
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
            "absentMetas",
            "unchangedTargets",
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
    for name, expected in {**result["preservedMetas"], **result["unchangedTargets"]}.items():
        check(ROOT / name, expected)
    for name in result["absentMetas"]:
        require(not (ROOT / name).exists(), "Previously absent meta appeared: " + name)
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
    for name, expected in {**result["preservedMetas"], **result["unchangedTargets"]}.items():
        check(ROOT / name, expected)
    for name in result["absentMetas"]:
        require(not (ROOT / name).exists(), "Previously absent meta appeared: " + name)
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
