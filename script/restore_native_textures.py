#!/usr/bin/env python3
"""Restore original Texture2D GPU payloads in a disposable recovered Unity project.

Matches names/dimensions AND decoded pixels, rejecting ambiguous differing native
payloads. Keeps every mip, sampler, format and color-space field. Reuses exported
GUIDs/fileIDs. Never touches the Steam installation or pristine recovery exports.
Run with audit-assets/.venv/bin/python (UnityPy and Pillow).
"""

from pathlib import Path
from collections import defaultdict
import argparse
import gc
import hashlib
import json
import re
import shutil
import sys
import UnityPy
from PIL import Image, ImageChops, ImageStat

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--project", type=Path, default=root / "unity-full")
parser.add_argument("--apply", action="store_true")
parser.add_argument("--reuse-plan", action="store_true")
args = parser.parse_args()
project = args.project.resolve()
if project.parent != root or project.name not in {"unity", "unity-full", "texture-probe"}:
    raise SystemExit("Only generated working projects within NativeH3VR are permitted")
report_dir = root / "build/textures" / project.name
report_dir.mkdir(parents=True, exist_ok=True)
plan_path = report_dir / "plan.json"
source = Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"


def safe_name(value):
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", value).casefold()


def signature(tree, raw):
    fields = {k: v for k, v in tree.items() if k not in {"image data", "m_StreamData"}}
    return hashlib.sha256(json.dumps(fields, sort_keys=True).encode() + raw).hexdigest()


def texture_yaml(tree, raw):
    lines = [
        "%YAML 1.1",
        "%TAG !u! tag:unity3d.com,2011:",
        "--- !u!28 &2800000",
        "Texture2D:",
        "  serializedVersion: 2",
        "  m_ObjectHideFlags: 0",
        "  m_PrefabParentObject: {fileID: 0}",
        "  m_PrefabInternal: {fileID: 0}",
        "  m_Name: " + json.dumps(tree["m_Name"]),
        "  m_ImageContentsHash:",
        "    serializedVersion: 2",
        "    Hash: 00000000000000000000000000000000",
    ]
    for name in [
        "m_Width",
        "m_Height",
        "m_CompleteImageSize",
        "m_TextureFormat",
        "m_MipCount",
        "m_IsReadable",
        "m_AlphaIsTransparency",
        "m_ImageCount",
        "m_TextureDimension",
    ]:
        lines.append("  " + name + ": " + str(int(tree.get(name, 0))))
    lines.append("  m_TextureSettings:")
    for name, value in tree["m_TextureSettings"].items():
        lines.append("    " + name + ": " + str(value))
    for name in ["m_LightmapFormat", "m_ColorSpace"]:
        lines.append("  " + name + ": " + str(tree.get(name, 0)))
    lines += [
        "  image data: " + str(len(raw)),
        "  _typelessdata: " + raw.hex(),
        "  m_StreamData:",
        "    offset: 0",
        "    size: 0",
        "    path:",
    ]
    return "\n".join(lines) + "\n"


paths = sorted(
    {
        source / item["path"]
        for item in json.loads((root / "audit-assets/inventory.json").read_text())["files"]
    }
)
if args.reuse_plan:
    plan = json.loads(plan_path.read_text())
    if plan["project"] != str(project):
        raise SystemExit("Texture plan belongs to another project")
else:
    candidates = defaultdict(list)
    image_count = 0
    for png in (project / "Assets").rglob("*.png"):
        meta = png.with_suffix(".png.meta")
        if not meta.exists() or "TextureImporter:" not in meta.read_text():
            continue
        with Image.open(png) as image:
            size = image.size
        keys = {png.stem.casefold(), re.sub(r" \d+$", "", png.stem.casefold())}
        for key in keys:
            candidates[(key, *size)].append(png)
        image_count += 1
    print("PNG candidates:", image_count, flush=True)
    matches = defaultdict(dict)
    errors = []
    for source_path in paths:
        env = UnityPy.load(str(source_path))
        count = 0
        for obj in env.objects:
            if obj.type.name != "Texture2D":
                continue
            try:
                tree = obj.read_typetree()
                group = candidates.get(
                    (safe_name(tree["m_Name"]), tree["m_Width"], tree["m_Height"]), []
                )
                if not group:
                    continue
                data = obj.read()
                pixels = data.image.convert("RGBA")
                raw = data.get_image_data()
                sig = signature(tree, raw)
                for png in group:
                    with Image.open(png) as exported:
                        # Different BC software decoders may round by one 8-bit unit.
                        diff = ImageChops.difference(pixels, exported.convert("RGBA"))
                        maximum = max(hi for lo, hi in diff.getextrema())
                    if maximum > 1:
                        continue
                    relative = str(png.relative_to(project))
                    matches[relative][sig] = {
                        "source": str(source_path.relative_to(source)),
                        "collection": obj.assets_file.name,
                        "pathID": obj.path_id,
                        "signature": sig,
                        "payloadBytes": len(raw),
                        "format": tree["m_TextureFormat"],
                        "mips": tree["m_MipCount"],
                        "maxDecodedPixelDifference": maximum,
                    }
                    count += 1
            except Exception as exc:
                errors.append(
                    {"source": str(source_path), "pathID": obj.path_id, "error": str(exc)}
                )
        print(source_path.name, "matches", count, "mapped PNGs", len(matches), flush=True)
        del env
        gc.collect()
    unique = {
        path: next(iter(choices.values())) for path, choices in matches.items() if len(choices) == 1
    }
    ambiguous = {
        path: list(choices.values()) for path, choices in matches.items() if len(choices) > 1
    }
    plan = {
        "project": str(project),
        "pngCount": image_count,
        "unique": unique,
        "ambiguous": ambiguous,
        "unmatchedCount": image_count - len(matches),
        "errors": errors,
    }
    plan_path.write_text(json.dumps(plan, indent=2) + "\n")
    print(
        "Plan:",
        len(unique),
        "unambiguous,",
        len(ambiguous),
        "ambiguous,",
        plan["unmatchedCount"],
        "unmatched",
        flush=True,
    )

if not args.apply:
    sys.exit(0)

by_source = defaultdict(lambda: defaultdict(list))
for target, entry in plan["unique"].items():
    by_source[entry["source"]][(entry["collection"], entry["pathID"])].append((target, entry))
converted = {}
for relative_source, entries in by_source.items():
    env = UnityPy.load(str(source / relative_source))
    for obj in env.objects:
        targets = entries.get((obj.assets_file.name, obj.path_id))
        if not targets:
            continue
        tree = obj.read_typetree()
        raw = obj.read().get_image_data()
        sig = signature(tree, raw)
        content = texture_yaml(tree, raw)
        for target, expected in targets:
            if expected["signature"] != sig:
                raise RuntimeError("Source changed since plan: " + target)
            png = project / target
            asset = png.with_suffix(".texture2D")
            if not png.exists() and asset.exists():
                # Resuming a completed conversion; keep its existing GUID.
                meta = asset.with_suffix(".texture2D.meta").read_text()
            else:
                if shutil.disk_usage(project).free < max(18 * 1024**3, len(content) * 3):
                    raise RuntimeError("Stopping texture conversion below 18 GiB free")
                meta = png.with_suffix(".png.meta").read_text()
                asset.write_text(content)
                guid = re.search(r"^guid: ([0-9a-f]{32})$", meta, re.M)[1]
                bundle = re.search(r"^  assetBundleName:.*$", meta, re.M)
                variant = re.search(r"^  assetBundleVariant:.*$", meta, re.M)
                asset.with_suffix(".texture2D.meta").write_text(
                    "fileFormatVersion: 2\nguid: " + guid + "\nNativeFormatImporter:\n"
                    "  externalObjects: {}\n  mainObjectFileID: 2800000\n"
                    "  userData: Original native texture payload; NativeH3VR\n"
                    + (bundle[0] if bundle else "  assetBundleName:")
                    + "\n"
                    + (variant[0] if variant else "  assetBundleVariant:")
                    + "\n"
                )
                png.unlink()
                png.with_suffix(".png.meta").unlink()
            guid = re.search(r"^guid: ([0-9a-f]{32})$", meta, re.M)[1]
            converted[guid] = target
    print("Restored", relative_source, "textures:", len(converted), flush=True)
    del env
    gc.collect()

# Native serialized asset references use type 2. Sprite references remain separate.
pattern = re.compile(r"(\{fileID: (-?\d+), guid: )([0-9a-f]{32})(, type: )3(\})")
rewritten = 0
non_texture_references = []
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
    text = file.read_text(errors="surrogateescape")
    if "guid:" not in text:
        continue

    def replace(match):
        global rewritten
        if match[3] not in converted:
            return match[0]
        if match[2] != "2800000":
            non_texture_references.append({"file": str(file), "reference": match[0]})
            return match[0]
        rewritten += 1
        return match[1] + match[3] + match[4] + "2" + match[5]

    result = pattern.sub(replace, text)
    if result != text:
        file.write_text(result, errors="surrogateescape")
report = {
    "converted": len(converted),
    "referencesRewritten": rewritten,
    "unexpectedReferences": non_texture_references,
    "guids": converted,
}
(report_dir / "conversion.json").write_text(json.dumps(report, indent=2) + "\n")
print(
    "Converted:",
    len(converted),
    "references:",
    rewritten,
    "unexpected:",
    len(non_texture_references),
)
if non_texture_references:
    raise SystemExit("Unexpected subasset references require repair before building")
