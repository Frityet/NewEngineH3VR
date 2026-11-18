#!/usr/bin/env python3
"""Relate private raw shader exports to the original Unity project GUIDs."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def child(root, name):
    path = root / name
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(root) or path != resolved:
        raise ValueError(f"Unsafe receipt path: {name}")
    return resolved


def check_hash(path, expected):
    if digest(path) != expected:
        raise ValueError(f"Receipt hash mismatch: {path}")


def original_key(row, path_id):
    bundle = Path(row["source"]).name.casefold() if row["bundleEntry"] is not None else ""
    # Both imported and raw AssetRipper identify members by FilenameUtils' archive leaf.
    member = row["serializedName"].replace("\\", "/").rsplit("/", 1)[-1].casefold()
    return bundle, member, int(path_id)


def project_key(row):
    chain = row["BundleChain"]
    if len(chain) not in (1, 2) or chain[-1] != "GameBundle":
        raise ValueError(f"Cannot prove the origin of nested shader collection {row['CollectionName']}")
    bundle = chain[0].casefold() if len(chain) == 2 else ""
    return bundle, row["CollectionName"].casefold(), int(row["PathId"])


def find_links(recovery, workspace, steam):
    receipt = json.loads((recovery / "recovery-receipt.json").read_text())
    if receipt["validationState"] != "raw-export-unvalidated":
        raise ValueError("Expected a receipt from recover-shaders")
    identity = child(recovery, receipt["exportIdentityManifest"])
    check_hash(identity, receipt["exportIdentityManifestSha256"])
    inventory_path = child(recovery, receipt["inputManifest"])
    check_hash(inventory_path, receipt["inputManifestSha256"])
    inventory = json.loads(inventory_path.read_text())
    if inventory["schemaVersion"] != 3:
        raise ValueError("Shader input receipt does not record the original serialized member names")

    project = workspace / "recovery/ExportedProject"
    map_path = workspace / "recovery/AuxiliaryFiles/shader-identity-v2.json"
    extract = json.loads((workspace / ".conversion/extract.json").read_text())
    if extract["Step"] != "extract":
        raise ValueError("Expected an extraction receipt")
    check_hash(map_path, extract["Files"][str(map_path)])
    target_map = json.loads(map_path.read_text())
    if target_map["schemaVersion"] != 1:
        raise ValueError("Unsupported project shader identity map")

    data = child(steam, "h3vr_Data")
    source_ids = {}
    verified = {}
    for entry in inventory["files"]:
        source = entry["source"]
        if source not in verified:
            check_hash(child(data, source), entry["sha256"])
            verified[source] = entry["sha256"]
        elif verified[source] != entry["sha256"]:
            raise ValueError(f"Inconsistent input hashes for {source}")
        for path_id in entry["pathIds"]:
            key = original_key(entry, path_id)
            if key in source_ids:
                raise ValueError(f"Ambiguous original serialized object: {key}")
            source_ids[key] = (source, entry["bundleEntry"], entry["sha256"])

    raw_rows = {}
    for row in json.loads(identity.read_text())["bindings"]:
        key = original_key(row, row["PathId"])
        source = (row["source"], row["bundleEntry"], row["sourceSha256"])
        if source_ids.get(key) != source or key in raw_rows:
            raise ValueError(f"Unproven raw shader identity: {key}")
        raw_rows[key] = row

    raw_project = recovery / "assetripper-export/ExportedProject"
    links = []
    missing = []
    used = set()
    for target in target_map["shaders"]:
        key = project_key(target)
        source = raw_rows.get(key)
        if source is None:
            missing.append(target["ExportPath"])
            continue
        if key in used:
            raise ValueError(f"Ambiguous recovered project identity: {key}")
        used.add(key)
        target_path = child(project, target["ExportPath"])
        target_hash = extract["Files"][str(target_path)]
        target_meta_hash = extract["Files"][str(target_path) + ".meta"]
        check_hash(target_path, target_hash)
        check_hash(Path(str(target_path) + ".meta"), target_meta_hash)
        raw_path = child(raw_project, source["rawPath"])
        raw_hash = receipt["sha256"]["ExportedProject/" + source["rawPath"]]
        check_hash(raw_path, raw_hash)
        links.append({**source, "rawSha256": raw_hash, "projectGuid": target["Guid"],
                      "projectPath": target["ExportPath"], "projectSha256": target_hash,
                      "projectMetaSha256": target_meta_hash})

    return {
        "schemaVersion": 1,
        "validationState": "identity-only-unvalidated",
        "extractionFingerprint": extract["Fingerprint"],
        "shaderInputManifestSha256": receipt["inputManifestSha256"],
        "rawIdentityManifestSha256": receipt["exportIdentityManifestSha256"],
        "links": links,
        "unmappedProjectPaths": missing,
        "unusedRawObjects": len(raw_rows) - len(used),
    }


def matched_shaders(result, recovery, repair_keywords):
    if not repair_keywords:
        for row in result["links"]:
            yield row, None
        return

    from keyword_repair import load_objects

    receipt = json.loads((recovery / "recovery-receipt.json").read_text())
    inventory_path = child(recovery, receipt["inputManifest"])
    check_hash(inventory_path, result["shaderInputManifestSha256"])
    inventory = json.loads(inventory_path.read_text())
    inputs = {(row["source"], row["bundleEntry"]): row for row in inventory["files"]}
    groups = {}
    for row in result["links"]:
        original = inputs[(row["source"], row["bundleEntry"])]
        groups.setdefault(original["copy"], []).append((row, original["copySha256"]))
    input_root = child(recovery, "shader-inputs")
    for copy_name, shaders in groups.items():
        copy = child(input_root, copy_name)
        check_hash(copy, shaders[0][1])
        original_assets = load_objects(copy)
        for row, _ in shaders:
            yield row, original_assets[row["PathId"]]


def stage_candidate(result, recovery, workspace, destination, repair_keywords):
    project = workspace / "recovery/ExportedProject"
    raw = recovery / "assetripper-export/ExportedProject"
    if result["unmappedProjectPaths"] or not result["links"]:
        raise ValueError("The raw source cannot be staged until all project shaders have identities")
    if destination.exists() or any(destination.is_relative_to(root) for root in (recovery, workspace)):
        raise ValueError("Choose a new candidate shader overlay outside both workspaces")
    if repair_keywords:
        from keyword_repair import repair

    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".shader-candidate-", dir=destination.parent) as temporary:
        candidate = Path(temporary) / "candidate"
        candidate.mkdir()
        patches = []
        for row, original in matched_shaders(result, recovery, repair_keywords):
            target = candidate / row["projectPath"]
            metadata = Path(str(target) + ".meta")
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(child(raw, row["rawPath"]), target)
            shutil.copyfile(child(project, row["projectPath"] + ".meta"), metadata)
            check_hash(target, row["rawSha256"])
            check_hash(metadata, row["projectMetaSha256"])
            if original is not None:
                patched, passes = repair(target.read_bytes().decode("utf-8"), original)
                if passes:
                    target.write_bytes(patched.encode("utf-8"))
                    patches.append({"path": row["projectPath"], "before": row["rawSha256"],
                                    "after": digest(target), "passes": passes})
        candidate_receipt = dict(result, keywordRepairs=patches)
        (candidate / "shader-overlay-candidate.json").write_text(json.dumps(candidate_receipt, indent=2) + "\n")
        candidate.rename(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recovery", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--steam-install", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--overlay-candidate", type=Path, help="Stage identity-matched raw shaders for private diagnosis")
    parser.add_argument("--repair-exclusive-keywords", action="store_true",
                        help="Use original Direct3D variants to repair proven one-of keyword groups")
    args = parser.parse_args()
    if args.repair_exclusive_keywords and not args.overlay_candidate:
        parser.error("--repair-exclusive-keywords requires --overlay-candidate")
    recovery, workspace, steam = (path.resolve() for path in (args.recovery, args.workspace, args.steam_install))
    output = args.output.resolve()
    if output.is_relative_to(steam) or output.is_relative_to(workspace / "recovery"):
        parser.error("Place correspondence output outside Steam and the recorded project")
    try:
        if output.exists():
            raise FileExistsError(output)
        result = find_links(recovery, workspace, steam)
        with output.open("x") as stream:
            json.dump(result, stream, indent=2)
            stream.write("\n")
        if args.overlay_candidate:
            candidate = args.overlay_candidate.resolve()
            if candidate.is_relative_to(steam):
                raise ValueError("Candidate overlay must be outside Steam")
            stage_candidate(result, recovery, workspace, candidate, args.repair_exclusive_keywords)
    except (ImportError, OSError, KeyError, ValueError) as error:
        parser.exit(1, str(error) + "\n")
    linked = len(result["links"])
    missing = len(result["unmappedProjectPaths"])
    print(f"Linked {linked} shader objects, {missing} unmatched project shaders. Rendering remains unvalidated.")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
