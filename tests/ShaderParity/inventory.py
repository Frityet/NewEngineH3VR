#!/usr/bin/env python3
"""Compare serialized shader metadata from local Unity player Data directories.

Requires UnityPy 1.25.4. Output contains local game asset metadata. Keep it
outside version control. This does not export code or test GPU rendering.
"""
from argparse import ArgumentParser
from collections import Counter, defaultdict
import json
from pathlib import Path
from struct import unpack_from

import UnityPy
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

from player_archive import PlayerArchive


SHADER_TYPES = ("Shader", "ComputeShader")
RENDER_TYPES = {"Shader", "ComputeShader", "Material"}
STAGES = ("Vertex", "Fragment", "Geometry", "Hull", "Domain", "RayTracing")
STOCK_PARSE_BLOB = TypeTreeNode.parse_blob.__func__


def parse_67_blob(cls, reader, version):
    # The Unity 6.7 v26 file embeds a v33 tree, including fields UnityPy cannot
    # decode yet. Skip the size-prefixed tree and let UnityPy use its class
    # database for the ordinary engine types being read here.
    start = reader.Position
    if version == 26:
        reader.Position = start - 4
        size = reader.read_int()
        if reader.read_bytes(4) == b"mhtt" and reader.read_int() == 33:
            reader.Position = start + size
            return None
    reader.Position = start
    return STOCK_PARSE_BLOB(cls, reader, version)


def unityfs9(stream):
    archive = PlayerArchive(stream)
    env = UnityPy.Environment()
    for name, (_, _, flags) in archive.members.items():
        if flags & 4:
            env.load_file(archive.read(name), name=name)
    return env


def load_container(stream):
    signature = stream.read(12)
    stream.seek(0)
    if signature[:8] == b"UnityFS\0" and unpack_from(">I", signature, 8)[0] == 9:
        try:
            TypeTreeNode.parse_blob = classmethod(parse_67_blob)
            return unityfs9(stream)
        finally:
            TypeTreeNode.parse_blob = classmethod(STOCK_PARSE_BLOB)
    return UnityPy.load(stream)


def containers(root):
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.suffix.lower() in {".ress", ".resource"}:
            continue
        if path.name.startswith("unity_") or path.suffix == ".assets":
            yield path
            continue
        if path.parent == root and (path.name == "globalgamemanagers" or path.name.startswith("level")):
            yield path
            continue
        with path.open("rb") as stream:
            if stream.read(8).rstrip(b"\x00") in {b"UnityFS", b"UnityRaw", b"UnityWeb", b"UnityArc"}:
                yield path


def pointer(ptr):
    if not ptr:
        return None
    owner = ptr.assetsfile
    if not ptr.m_FileID:
        path = owner.name
    else:
        path = owner.externals[ptr.m_FileID - 1].path
    return {"file": path, "pathId": ptr.m_PathID}


def graphics(data):
    form = data.m_ParsedForm
    if form is None:
        raise ValueError("shader has no serialized parsed form")
    subshaders = []
    for subshader in form.m_SubShaders:
        passes = []
        for pass_data in subshader.m_Passes:
            stages = {}
            for name in STAGES:
                program = getattr(pass_data, "prog" + name, None)
                if program is not None:
                    subprograms = program.m_SubPrograms or [
                        sub for tier in program.m_PlayerSubPrograms or [] for sub in tier
                    ]
                    counts = Counter(sub.m_GpuProgramType for sub in subprograms)
                    if counts:
                        stages[name] = counts
            passes.append({
                "name": pass_data.m_State.m_Name,
                "kind": pass_data.m_Type,
                "stageMask": pass_data.m_ProgramMask,
                "usePass": pass_data.m_UseName,
                "tags": dict(pass_data.m_State.m_Tags.tags),
                "stages": stages,
            })
        subshaders.append({"tags": dict(subshader.m_Tags.tags), "passes": passes})
    return {
        "name": form.m_Name or data.m_Name,
        "fallback": form.m_FallbackName,
        "platforms": data.platforms,
        "programBlobBytes": len(data.compressedBlob or data.m_SubProgramBlob or []),
        "subshaders": subshaders,
        "properties": sorted(prop.m_Name for prop in form.m_PropInfo.m_Props),
    }


def compute(data):
    def kernel_programs(kernel):
        variants = getattr(kernel, "uniqueVariants", None) or [kernel]
        return sorted(len(variant.code or []) for variant in variants)

    return {
        "name": data.m_Name,
        "kernels": sorted({kernel.name for kernel in data.kernels or []} |
                          {kernel.name for variant in data.variants or [] for kernel in variant.kernels}),
        "renderers": [
            {"renderer": variant.targetRenderer, "level": variant.targetLevel,
             "kernels": {kernel.name: kernel_programs(kernel) for kernel in variant.kernels}}
            for variant in data.variants or []
        ],
    }


def material(data):
    saved = data.m_SavedProperties
    return {
        "name": data.m_Name,
        "shader": pointer(data.m_Shader),
        "queue": data.m_CustomRenderQueue,
        "keywords": data.m_ShaderKeywords if data.m_ShaderKeywords is not None else data.m_ValidKeywords,
        "disabledPasses": data.disabledShaderPasses,
        "floats": dict(saved.m_Floats),
        "colors": {key: [value.r, value.g, value.b, value.a] for key, value in saved.m_Colors},
        "textures": {key: pointer(value.m_Texture) for key, value in saved.m_TexEnvs},
    }


READ = {"Shader": graphics, "ComputeShader": compute, "Material": material}


def file_key(path):
    return path.replace("\\", "/").rsplit("/", 1)[-1].lower()


def inventory(root):
    rows = defaultdict(list)
    errors = []
    files = []
    for path in containers(root):
        relative = path.relative_to(root).as_posix()
        try:
            with path.open("rb") as stream:
                env = load_container(stream)
                objects = env.objects
                parsed = sorted({(obj.assets_file.name, obj.assets_file.unity_version) for obj in objects})
                files.append({"path": relative, "files": [list(item) for item in parsed]})
                for obj in objects:
                    kind = obj.type.name
                    if kind not in RENDER_TYPES:
                        continue
                    collection = relative if obj.assets_file.name == path.name else obj.assets_file.name
                    identity = {"container": relative, "file": collection, "pathId": obj.path_id}
                    try:
                        value = READ[kind](obj.parse_as_object())
                    except Exception as exc:
                        errors.append({**identity, "kind": kind, "error": str(exc)})
                        continue
                    rows[kind].append({**identity, **value})
        except Exception as exc:
            errors.append({"container": relative, "error": str(exc)})
    shaders = defaultdict(list)
    for row in rows["Shader"]:
        shaders[(file_key(row["file"]), row["pathId"])].append(row)
    for row in rows["Material"]:
        ref = row["shader"]
        row["shaderName"] = None
        if ref is None:
            continue
        candidates = shaders.get((file_key(ref["file"]), ref["pathId"]), [])
        local = [shader for shader in candidates if shader["container"] == row["container"]]
        names = {shader["name"] for shader in local or candidates}
        if len(names) == 1:
            row["shaderName"] = next(iter(names))
    return {"containers": files, "rows": rows, "errors": errors}


def pass_key(entry):
    tags = {key.casefold(): value for key, value in entry["tags"].items()}
    return entry["name"].casefold(), tags.get("lightmode", "").casefold(), entry["kind"], entry["usePass"]


def shader_assignment(row):
    if row["shaderName"]:
        return row["shaderName"]
    if row["shader"]:
        pointer = row["shader"]
        return f'{file_key(pointer["file"])}:{pointer["pathId"]}'
    return "<null>"


def material_groups(rows):
    groups = defaultdict(Counter)
    for row in rows:
        # Streaming Asset CAB names are regenerated by the target editor.
        # Built scene and resource collections retain their serialized filenames.
        container = row["container"]
        location = container if container.startswith("StreamingAssets/") else file_key(row["file"])
        groups[(location, row["name"])][shader_assignment(row)] += 1
    return groups


def compare(stock, converted):
    original = stock["rows"]
    target = converted["rows"]
    result = {}
    for kind in SHADER_TYPES:
        by_name = defaultdict(list)
        for row in target[kind]:
            by_name[row["name"]].append(row)
        original_names = {row["name"] for row in original[kind]}
        matched = []
        for row in original[kind]:
            name = row["name"]
            candidates = by_name.get(name, [])
            match = {"name": row["name"], "file": row["file"], "pathId": row["pathId"]}
            legacy_name = "Legacy Shaders/" + name
            if not candidates and kind == "Shader" and name.startswith("Particles/") and legacy_name in by_name:
                match["legacyAlias"] = legacy_name
                candidates = by_name[legacy_name]
            match["candidates"] = [
                {key: candidate[key] for key in ("container", "file", "pathId")} for candidate in candidates
            ]
            if kind == "Shader" and candidates:
                def passes(shader):
                    return {pass_key(p) for s in shader["subshaders"] for p in s["passes"]}

                match["missingPasses"] = sorted(passes(row) - set().union(*(passes(c) for c in candidates)))
            if kind == "ComputeShader" and candidates:
                match["missingKernels"] = sorted(set(row["kernels"]) - {
                    kernel for c in candidates for kernel in c["kernels"]
                })
            matched.append(match)
        result[kind] = {"stock": matched, "newNames": sorted(by_name.keys() - original_names)}
    before_groups = material_groups(original["Material"])
    after_groups = material_groups(target["Material"])
    assignments = Counter()
    different = []
    for key in sorted(before_groups.keys() | after_groups.keys()):
        before = before_groups.get(key, Counter())
        after = after_groups.get(key, Counter())
        if len(before) == len(after) == 1 and before.total() == after.total():
            assignments[next(iter(before)), next(iter(after))] += before.total()
        if before != after:
            location, name = key
            different.append({"location": location, "name": name, "stock": before, "converted": after})
    result["materialAssignments"] = [
        {"stock": before, "converted": after, "stockObjects": count}
        for (before, after), count in sorted(assignments.items())
    ]
    result["materialGroupDifferences"] = different
    return result


def main():
    parser = ArgumentParser(description=__doc__)
    for key in ("stock", "builtin", "urp"):
        parser.add_argument("--" + key, type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--only", choices=("stock", "builtin", "urp"), help="Scan one player without comparing")
    args = parser.parse_args()
    selection = (args.only,) if args.only else ("stock", "builtin", "urp")
    for name in selection:
        path = getattr(args, name)
        if path is None or not path.is_dir():
            parser.error(f"--{name} must point to a Unity player Data directory")
        if args.out.resolve().is_relative_to(path.resolve()):
            parser.error("Output must be outside every player Data directory")
    args.out.mkdir(parents=True, exist_ok=True)
    inventories = {}
    for name in selection:
        data = inventories[name] = inventory(getattr(args, name))
        (args.out / f"{name}.json").write_text(json.dumps(data, indent=2) + "\n")
        print(name, {kind: len(rows) for kind, rows in data["rows"].items()}, "errors", len(data["errors"]), flush=True)
    if args.only:
        return
    for name in ("builtin", "urp"):
        report = compare(inventories["stock"], inventories[name])
        (args.out / f"stock-vs-{name}.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
