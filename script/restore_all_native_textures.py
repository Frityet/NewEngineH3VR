#!/usr/bin/env python3
"""Restore exact mapped GPU payloads as compact Unity native binary assets."""

from pathlib import Path
from collections import defaultdict
import argparse
import gc
import hashlib
import json
import os
import re
import shutil
import UnityPy
from native_texture_binary import native_file

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--project", type=Path, default=root / "unity-full")
args = parser.parse_args()
project = args.project.resolve()
if project != root / "unity-full":
    raise SystemExit("Exact identity maps belong only to the full working export")
source = Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
by_source = defaultdict(dict)
for kind, class_id, suffix, file_id in [
    ("texture2d", 28, ".texture2D", 2800000),
    ("cubemap", 89, ".cubemap", 8900000),
]:
    for row in json.loads(
        (root / f"build/asset-identities/full-{kind}-identity-map.json").read_text()
    ):
        row.update(class_id=class_id, suffix=suffix, file_id=file_id)
        key = (row["collection"].casefold(), row["path_id"])
        if key in by_source[row["source"]]:
            raise RuntimeError("Duplicate original texture identity")
        by_source[row["source"]][key] = row


# Replacing existing hex YAML first recovers disk space before adding new assets.
def saving(item):
    return sum(
        (project / row["exported_path"]).with_suffix(row["suffix"]).stat().st_size
        for row in item[1].values()
        if (project / row["exported_path"]).with_suffix(row["suffix"]).exists()
    )


records = []
references = {}
for original, mapping in sorted(by_source.items(), key=saving, reverse=True):
    env = UnityPy.load(str(source / original))
    found = set()
    for obj in env.objects:
        key = (obj.assets_file.name.casefold(), obj.path_id)
        row = mapping.get(key)
        if row is None:
            continue
        found.add(key)
        if obj.type.value != row["class_id"]:
            raise RuntimeError("Wrong native texture type: " + str(row))
        old = project / row["exported_path"]
        dest = old.with_suffix(row["suffix"])
        old_meta = old.with_suffix(old.suffix + ".meta")
        dest_meta = dest.with_suffix(dest.suffix + ".meta")
        meta = (old_meta if old_meta.exists() else dest_meta).read_text()
        if re.search(r"^guid: ([0-9a-f]{32})$", meta, re.M)[1] != row["guid"]:
            raise RuntimeError("Exported GUID changed: " + str(old))
        tree = obj.read_typetree()
        raw = obj.read().get_image_data()
        payload = native_file(tree, raw, row["class_id"])
        if shutil.disk_usage(project).free < max(12 * 1024**3, len(payload) * 3):
            raise RuntimeError(
                "Stopping below 12 GiB free; rerun after reclaiming generated caches"
            )
        temporary = dest.with_suffix(dest.suffix + ".native-tmp")
        temporary.write_bytes(payload)
        os.replace(temporary, dest)
        bundle = re.search(r"^  assetBundleName:.*$", meta, re.M)
        variant = re.search(r"^  assetBundleVariant:.*$", meta, re.M)
        dest_meta.write_text(
            "fileFormatVersion: 2\nguid: " + row["guid"] + "\nNativeFormatImporter:\n"
            "  externalObjects: {}\n  mainObjectFileID: " + str(row["file_id"]) + "\n"
            "  userData: Exact original GPU payload in Unity 2020 native binary\n"
            + (bundle[0] if bundle else "  assetBundleName:")
            + "\n"
            + (variant[0] if variant else "  assetBundleVariant:")
            + "\n"
        )
        if old.exists():
            old.unlink()
        if old_meta.exists():
            old_meta.unlink()
        references[row["guid"]] = row["file_id"]
        records.append(
            {
                "path": str(dest.relative_to(project)),
                "guid": row["guid"],
                "source": original,
                "collection": row["collection"],
                "pathID": obj.path_id,
                "classID": row["class_id"],
                "format": tree["m_TextureFormat"],
                "mips": tree["m_MipCount"],
                "rawBytes": len(raw),
                "binaryBytes": len(payload),
                "payloadSha256": hashlib.sha256(raw).hexdigest(),
            }
        )
    if found != set(mapping):
        raise RuntimeError(
            "Missing original texture identities in " + original + ": " + str(set(mapping) - found)
        )
    print(original, "native assets:", len(records), flush=True)
    del env
    gc.collect()

pattern = re.compile(r"(\{fileID: (-?\d+), guid: )([0-9a-f]{32})(, type: )3(\})")
rewritten = [0]
unexpected = []
for file in (project / "Assets").rglob("*"):
    if file.suffix not in {
        ".unity",
        ".prefab",
        ".mat",
        ".asset",
        ".controller",
        ".overrideController",
    }:
        continue
    data = file.read_text(errors="surrogateescape")
    if "guid:" not in data:
        continue

    def replace(match):
        expected = references.get(match[3])
        if expected is None:
            return match[0]
        if int(match[2]) != expected:
            unexpected.append(
                {"file": str(file), "reference": match[0], "expectedFileID": expected}
            )
            return match[0]
        rewritten[0] += 1
        return match[1] + match[3] + match[4] + "2" + match[5]

    updated = pattern.sub(replace, data)
    if updated != data:
        file.write_text(updated, errors="surrogateescape")
out = root / "build/textures/unity-full/native-binary-conversion.json"
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(
    json.dumps(
        {
            "project": str(project),
            "assets": records,
            "referencesRewritten": rewritten[0],
            "unexpectedReferences": unexpected,
        },
        indent=2,
    )
    + "\n"
)
print(
    "Restored",
    len(records),
    "native textures; references:",
    rewritten[0],
    "unexpected:",
    len(unexpected),
)
if unexpected:
    raise SystemExit("Unexpected texture references require review")
