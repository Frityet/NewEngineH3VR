#!/usr/bin/env python3
"""Read-only gate for the Unity content proof, its exact plan, inputs and outputs."""

from pathlib import Path
import argparse
import hashlib
import json
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "shaders"))
from verify_shader_proof_logs import inspect as inspect_compiler_log, POLICY_PATH

LEGACY_MARKER = "native-bundle-verification.json"
CONTENT_MARKER = "native-bundle-content-verification.json"
COMPILER_RECEIPT = "native-bundle-compiler-receipt.json"
COMPILER_LOG = "native-bundle-compiler.log"


def digest(path):
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(data)
    return checksum.hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def inside(directory, name):
    require(isinstance(name, str) and bool(name), "Empty/non-string artifact path")
    path = (directory / name).resolve()
    require(
        not Path(name).is_absolute() and path.is_relative_to(directory.resolve()),
        f"Artifact path escapes directory: {name}",
    )
    return path


def fingerprint(record, path):
    require(path.is_file(), f"Missing verified file: {path}")
    require(record.get("bytes") == path.stat().st_size, f"Verified file size changed: {path}")
    require(record.get("sha256") == digest(path), f"Verified file hash changed: {path}")


def verify(directory, plan_path, project):
    directory, plan_path, project = Path(directory), Path(plan_path), Path(project)
    plan = json.loads(plan_path.read_text())
    require(Path(plan["project"]).resolve() == project.resolve(), "Bundle plan project mismatch")
    require(
        plan.get("issues") == [] and bool(plan.get("bundles")), "Bundle plan has issues/is empty"
    )
    names = [b["name"] for b in plan["bundles"]]
    require(len(set(names)) == len(names), "Duplicate planned bundle names")
    for bundle in plan["bundles"]:
        addresses = [a["address"].lower() for a in bundle["assets"]]
        require(
            addresses and len(addresses) == len(set(addresses)), "Empty/duplicate planned addresses"
        )
        dependencies = bundle.get("dependencies")
        require(
            isinstance(dependencies, list)
            and len(dependencies) == len(set(dependencies))
            and set(dependencies) <= set(names) - {bundle["name"]},
            "Missing/duplicate/unknown planned dependencies",
        )
    expected = {
        "platform": "StandaloneOSX",
        "graphics": "Metal",
        "bundles": len(names),
        "addresses": sum(len(b["assets"]) for b in plan["bundles"]),
        "missing": 0,
    }
    require(
        json.loads((directory / LEGACY_MARKER).read_text()) == expected,
        "Missing or stale bundle summary",
    )
    proof = json.loads((directory / CONTENT_MARKER).read_text())
    require(
        proof.get("schemaVersion") == 2 and proof.get("verified") is True,
        "A successful schema-2 content proof is required; run NativeBundleBuild.VerifyExisting",
    )
    for key in ("platform", "graphics", "bundles", "addresses"):
        require(proof.get(key) == expected[key], f"Content proof {key} mismatch")
    require(proof.get("planSha256") == digest(plan_path), "Bundle plan changed after verification")
    inventory = proof.get("bundleInventory")
    require(
        isinstance(inventory, list)
        and len(inventory) == len(names)
        and {b["name"] for b in inventory} == set(names),
        "Missing/incomplete observed bundle inventory",
    )
    by_name = {b["name"]: b for b in inventory}
    for bundle in plan["bundles"]:
        observed = by_name[bundle["name"]]
        require(
            observed.get("addresses") == sorted(a["address"].lower() for a in bundle["assets"]),
            "Observed native address set differs from original: " + bundle["name"],
        )
        require(
            observed.get("dependencies") == sorted(bundle["dependencies"]),
            "Observed native dependency graph differs from original: " + bundle["name"],
        )
    require(
        proof.get("mode") in ("build", "existing-output") and bool(proof.get("unityVersion")),
        "Missing verifier engine/provenance",
    )
    outputs = proof.get("outputs")
    require(isinstance(outputs, list), "Missing output fingerprints")
    expected_names = {n + suffix for n in names + [directory.name] for suffix in ("", ".manifest")}
    require(
        len(outputs) == len(expected_names) and {r["path"] for r in outputs} == expected_names,
        "Content proof output set differs from the complete plan/manifest",
    )
    for record in outputs:
        fingerprint(record, inside(directory, record["path"]))
    inputs = proof.get("managedInputs")
    require(isinstance(inputs, list) and bool(inputs), "Missing managed input fingerprints")
    require(len({r["path"] for r in inputs}) == len(inputs), "Duplicate managed fingerprints")
    for record in inputs:
        # Unity may return absolute package-source paths; these were captured by
        # the Editor. Relative Asset/Package paths are always project-relative.
        path = Path(record["path"])
        fingerprint(record, path if path.is_absolute() else project / path)
    recorded_inputs = {
        (Path(r["path"]) if Path(r["path"]).is_absolute() else project / r["path"]).resolve()
        for r in inputs
    }
    for path in (project / "Assets").rglob("*"):
        if path.is_file() and path.suffix.lower() in (".cs", ".dll", ".asmdef", ".asmref"):
            require(
                path.resolve() in recorded_inputs, f"New managed input after verification: {path}"
            )
            meta = Path(str(path) + ".meta")
            require(
                not meta.is_file() or meta.resolve() in recorded_inputs,
                f"New managed importer metadata after verification: {meta}",
            )
    dependencies = proof.get("sourceDependencies")
    require(
        isinstance(dependencies, list) and bool(dependencies), "Missing source dependency proof"
    )
    require(
        len({r["path"] for r in dependencies}) == len(dependencies), "Duplicate source dependencies"
    )
    require(
        all(isinstance(r.get("hash"), str) and len(r["hash"]) == 32 for r in dependencies),
        "Invalid Unity dependency fingerprints",
    )
    source_files = proof.get("sourceFiles")
    require(
        isinstance(source_files, list) and bool(source_files), "Missing raw source fingerprints"
    )
    require(
        len({r["path"] for r in source_files}) == len(source_files), "Duplicate source fingerprints"
    )
    # Built-in virtual assets have no disk file. Every existing dependency and
    # its meta must be represented, including files added since verification.
    expected_sources = {
        name
        for r in dependencies
        for name in (r["path"], r["path"] + ".meta")
        if (project / name).is_file()
    }
    require(
        {r["path"] for r in source_files} == expected_sources, "Source dependency file set changed"
    )
    for record in source_files:
        fingerprint(record, project / record["path"])
    for field, filename in (("compilerReceipt", COMPILER_RECEIPT), ("compilerLog", COMPILER_LOG)):
        record = proof.get(field)
        require(
            isinstance(record, dict) and record.get("path") == filename,
            "Missing bound clean compiler evidence",
        )
        fingerprint(record, directory / filename)
    receipt = json.loads((directory / COMPILER_RECEIPT).read_text())
    require(
        receipt.get("schemaVersion") == 1
        and receipt.get("clean") is True
        and bool(receipt.get("runId")),
        "Missing successful build-time compiler receipt; rebuild required",
    )
    require(
        receipt.get("compilerPolicySha256") == digest(POLICY_PATH),
        "Compiler diagnostic policy changed",
    )
    for field in (
        "planSha256",
        "unityVersion",
        "managedInputs",
        "sourceFiles",
        "outputs",
        "compilerLog",
    ):
        require(
            receipt.get(field) == proof.get(field),
            "Compiler receipt does not bind accepted " + field,
        )
    compiler = inspect_compiler_log(directory / COMPILER_LOG)
    require(
        compiler["passed"], "Compiler log contains errors: " + repr(compiler["diagnostics"][:5])
    )
    require(
        "NATIVEH3VR_COMPILER_BUILD_END " + receipt["runId"]
        in (directory / COMPILER_LOG).read_text(),
        "Compiler snapshot lacks the bound completed-build marker",
    )
    raw_path = Path(receipt.get("buildLogPath", ""))
    require(
        raw_path.is_absolute() and raw_path.is_file(), "Originating raw build log is unavailable"
    )
    raw_check = inspect_compiler_log(raw_path)
    require(
        raw_check["passed"],
        "Originating build log contains delayed compiler/errors: "
        + repr(raw_check["diagnostics"][:5]),
    )
    require(
        raw_path.read_text(errors="replace").startswith((directory / COMPILER_LOG).read_text()),
        "Originating raw build log was replaced/truncated; compiler snapshot prefix differs",
    )
    return proof


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--directory", type=Path, default=root / "unity-full/Assets/StreamingAssets"
    )
    parser.add_argument("--plan", type=Path, default=root / "build/native-bundle-plan.json")
    parser.add_argument("--project", type=Path, default=root / "unity-full")
    args = parser.parse_args()
    proof = verify(args.directory, args.plan, args.project)
    print(
        f"Verified {proof['bundles']} macOS bundles, {proof['addresses']} loaded addresses and all input/output hashes."
    )


if __name__ == "__main__":
    main()
