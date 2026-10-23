#!/usr/bin/env python3
"""Read-only audit of native GPU textures after Unity reserialization.

Run with audit-assets/.venv/bin/python. Hashes refer to compressed GPU bytes,
including all mip levels and cubemap faces; no pixels are decoded. Original
Steam assets are opened only for metadata comparison, never modified.
"""

import argparse
from collections import Counter, defaultdict
import gc
import hashlib
import json
from pathlib import Path
import re
import time

import UnityPy

ROOT = Path(__file__).resolve().parents[1]
FIELDS = (
    "m_Width",
    "m_Height",
    "m_CompleteImageSize",
    "m_IsReadable",
    "m_AlphaIsTransparency",
    "m_LightmapFormat",
    "m_ColorSpace",
)


def metadata(texture, class_id):
    result = {key: getattr(texture, key, None) for key in FIELDS}
    settings = texture.m_TextureSettings
    result["sampler"] = {
        key: getattr(settings, key, None) for key in ("m_FilterMode", "m_Aniso", "m_MipBias")
    }
    for axis in "UVW":
        key = "m_Wrap" + axis
        value = getattr(settings, key, None)
        result["sampler"][key] = value if value is not None else settings.m_WrapMode
    result["m_ImageCount"] = 6 if class_id == 89 else 1
    result["m_TextureDimension"] = 4 if class_id == 89 else 2
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=ROOT / "unity-full")
    parser.add_argument(
        "--manifest",
        type=Path,
        default=ROOT / "build/textures/unity-full/native-binary-conversion.json",
    )
    parser.add_argument(
        "--crunch-manifest", type=Path, default=ROOT / "build/textures/legacy-crunch-applied.json"
    )
    parser.add_argument(
        "--original-data",
        type=Path,
        default=Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data",
    )
    parser.add_argument(
        "--compaction-log", type=Path, default=ROOT / "build/texture-compaction.log"
    )
    parser.add_argument(
        "--report", type=Path, default=ROOT / "build/textures/postcompaction-verification.json"
    )
    args = parser.parse_args()
    if "NATIVE_TEXTURE_BINARY_COMPACTION_DONE " not in args.compaction_log.read_text():
        parser.error("Compaction has not reported completion; refusing to inspect mutable assets")
    started = time.time()
    rows = json.loads(args.manifest.read_text())["assets"]
    crunch = {row["path"]: row for row in json.loads(args.crunch_manifest.read_text())["textures"]}
    if len({row["path"] for row in rows}) != len(rows):
        raise ValueError("Duplicate asset path in baseline manifest")
    if not set(crunch).issubset({row["path"] for row in rows}):
        raise ValueError("Crunch overrides include unknown baseline paths")
    grouped = defaultdict(list)
    for row in rows:
        grouped[row["source"]].append(row)
    defaults = {}
    for class_id, name in ((28, "Texture2D"), (89, "Cubemap")):
        template = UnityPy.load(str(ROOT / "build/native-texture-template" / (name + ".asset")))
        defaults[class_id] = metadata(next(iter(template.objects)).read(), class_id)
    results, errors, adjustments = [], [], []
    total_bytes = 0
    for source, source_rows in grouped.items():
        original = UnityPy.load(str(args.original_data / source))
        originals = {
            (obj.assets_file.name.casefold(), obj.path_id): obj
            for obj in original.objects
            if obj.type.value in (28, 89)
        }
        for row in source_rows:
            record = {"path": row["path"], "guid": row["guid"], "errors": []}
            try:
                path = args.project / row["path"]
                override = crunch.get(row["path"])
                expected_format = override["nativeFormat"] if override else row["format"]
                expected_size = override["dxtBytes"] if override else row["rawBytes"]
                expected_hash = override["dxtSHA256"] if override else row["payloadSha256"]
                expected_mips = override["mips"] if override else row["mips"]
                expected_id = 8900000 if row["classID"] == 89 else 2800000
                with path.open("rb") as stream:
                    if stream.read(5) == b"%YAML":
                        raise ValueError("Still text YAML after compaction")
                meta = path.with_suffix(path.suffix + ".meta").read_text()
                for pattern, expected, label in (
                    (r"^guid: (\w+)$", row["guid"], "GUID"),
                    (r"^  mainObjectFileID: (\d+)$", str(expected_id), "meta mainObjectFileID"),
                ):
                    match = re.search(pattern, meta, re.M)
                    if not match or match[1] != expected:
                        record["errors"].append(label + " mismatch")
                if "\nNativeFormatImporter:" not in meta:
                    record["errors"].append("Importer is not NativeFormatImporter")
                env = UnityPy.load(str(path))
                objects = list(env.objects)
                matches = [obj for obj in objects if obj.type.value == row["classID"]]
                if len(matches) != 1:
                    raise ValueError(
                        "Expected exactly one texture object, got " + str(len(matches))
                    )
                obj = matches[0]
                tex = obj.read()
                raw = tex.get_image_data()
                digest = hashlib.sha256(raw).hexdigest()
                total_bytes += len(raw)
                actual = {
                    "fileID": obj.path_id,
                    "format": tex.m_TextureFormat,
                    "mips": tex.m_MipCount,
                    "rawBytes": len(raw),
                    "payloadSha256": digest,
                }
                expected = {
                    "fileID": expected_id,
                    "format": expected_format,
                    "mips": expected_mips,
                    "rawBytes": expected_size,
                    "payloadSha256": expected_hash,
                }
                record.update(actual)
                for key in expected:
                    if actual[key] != expected[key]:
                        record["errors"].append(
                            {"field": key, "expected": expected[key], "actual": actual[key]}
                        )
                if override:
                    offset = 0
                    mip_hashes = []
                    block_size = 8 if expected_format == 10 else 16
                    for mip in range(expected_mips):
                        width, height = max(1, tex.m_Width >> mip), max(1, tex.m_Height >> mip)
                        size = ((width + 3) // 4) * ((height + 3) // 4) * block_size
                        mip_hashes.append(hashlib.sha256(raw[offset : offset + size]).hexdigest())
                        offset += size
                    if mip_hashes != override["mipSHA256"] or offset != len(raw):
                        record["errors"].append("Crunch DXT per-mip hash or byte coverage mismatch")
                old_obj = originals[(row["collection"].casefold(), row["pathID"])]
                old = old_obj.read()
                expected_metadata = metadata(old, row["classID"])
                # native_texture_binary.py retains template defaults for fields
                # absent in Unity 5 (notably m_AlphaIsTransparency).
                for key, value in expected_metadata.items():
                    if value is None:
                        expected_metadata[key] = defaults[row["classID"]][key]
                if override:
                    expected_metadata["m_CompleteImageSize"] = override["dxtBytes"]
                actual_metadata = metadata(tex, row["classID"])
                # Unlike metadata(old), the new object must actually store the upgraded values.
                actual_metadata["m_ImageCount"] = tex.m_ImageCount
                actual_metadata["m_TextureDimension"] = tex.m_TextureDimension
                differences = {
                    key: {"expected": value, "actual": actual_metadata[key]}
                    for key, value in expected_metadata.items()
                    if actual_metadata[key] != value
                }
                if differences:
                    adjustments.append({"path": row["path"], "changes": differences})
                record["metadataMatches"] = not differences
                del env, obj, tex, raw, old
            except Exception as exc:
                record["errors"].append(type(exc).__name__ + ": " + str(exc))
            if record["errors"]:
                errors.append({"path": row["path"], "errors": record["errors"]})
            results.append(record)
        del originals, original
        gc.collect()
        print(
            f"{len(results)}/{len(rows)} assets, {len(errors)} errors, "
            f"{len(adjustments)} metadata changes: {source}",
            flush=True,
        )
    report = {
        "project": str(args.project.resolve()),
        "manifest": str(args.manifest),
        "crunchManifest": str(args.crunch_manifest),
        "assetsChecked": len(results),
        "manifestSha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
        "crunchManifestSha256": hashlib.sha256(args.crunch_manifest.read_bytes()).hexdigest(),
        "classCounts": dict(Counter(row["classID"] for row in rows)),
        "crunchOverrides": len(crunch),
        "payloadBytesChecked": total_bytes,
        "seconds": round(time.time() - started, 2),
        "errors": errors,
        "metadataChanges": adjustments,
        "passed": not errors and not adjustments,
        "assets": results,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    summary = {
        key: value
        for key, value in report.items()
        if key not in ("assets", "errors", "metadataChanges")
    }
    summary.update(
        errorCount=len(errors),
        metadataChangeCount=len(adjustments),
        firstErrors=errors[:10],
        firstMetadataChanges=adjustments[:10],
    )
    print(json.dumps(summary, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
