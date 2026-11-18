#!/usr/bin/env python3
"""Relate private raw shader exports to the original Unity project GUIDs."""
import argparse
import hashlib
import json
from pathlib import Path
import sys


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


def original_key(row):
    bundle = Path(row["source"]).name.casefold() if row["bundleEntry"] is not None else ""
    # Both imported and raw AssetRipper identify members by FilenameUtils' archive leaf.
    member = row["serializedName"].replace("\\", "/").rsplit("/", 1)[-1].casefold()
    return bundle, member, int(row["pathId"])


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
            key = original_key({**entry, "pathId": path_id})
            if key in source_ids:
                raise ValueError(f"Ambiguous original serialized object: {key}")
            source_ids[key] = (source, entry["bundleEntry"], entry["sha256"])

    raw_rows = {}
    for row in json.loads(identity.read_text())["bindings"]:
        key = original_key(row)
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
        check_hash(target_path, target_hash)
        check_hash(Path(str(target_path) + ".meta"), extract["Files"][str(target_path) + ".meta"])
        raw_path = child(raw_project, source["rawPath"])
        raw_hash = receipt["sha256"]["ExportedProject/" + source["rawPath"]]
        check_hash(raw_path, raw_hash)
        links.append({**source, "rawSha256": raw_hash, "projectGuid": target["Guid"],
                      "projectPath": target["ExportPath"], "projectSha256": target_hash})

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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recovery", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--steam-install", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
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
    except (OSError, KeyError, ValueError) as error:
        parser.exit(1, str(error) + "\n")
    linked = len(result["links"])
    missing = len(result["unmappedProjectPaths"])
    print(f"Linked {linked} shader objects, {missing} unmatched project shaders. Rendering remains unvalidated.")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
