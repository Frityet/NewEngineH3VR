#!/usr/bin/env python3
"""Prepare/verify one isolated PIP depth-clip state repair; never install it.

Run with audit-assets/.venv/bin/python (UnityPy). Only the admitted UnityFS8
LZ4 format is supported. Unchanged compressed blocks are copied verbatim;
neither shader programs nor the containing serialized file are recompiled.
"""

from __future__ import annotations
import argparse
from copy import deepcopy
import gc
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = "assets_resources_objectids_weaponry_ar15"
CAB = "CAB-d98205cd3e60321f23684452d96b0f22"
SHADER = "Unlit/PIPScope"
SOURCE_PID = -3051070195593120036
MODERN_PID = 5248756200144351081
SOURCE = (
    ROOT
    / "audit-assets/all-shader-inputs/010-0-StreamingAssets_assets_resources_objectids_weaponry_ar15.assets"
)
KIND = "newengine-pip-zclip-state-only"
PASS_NAMES = ["FORWARDBASE", "SHADOWCASTER"]


def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for data in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            h.update(data)
    return h.hexdigest()


def digest(data):
    return hashlib.sha256(bytes(data)).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def differences(old, new, path=""):
    if type(old) is not type(new):
        return [dict(path=path, before=old, after=new)]
    if old == new:
        return []
    if isinstance(old, dict):
        require(old.keys() == new.keys(), "Unexpected shader field addition/removal at " + path)
        return [
            row
            for key in old
            for row in differences(old[key], new[key], path + ("." if path else "") + key)
        ]
    if isinstance(old, list):
        require(len(old) == len(new), "Unexpected shader array resize at " + path)
        return [
            row
            for i in range(len(old))
            for row in differences(old[i], new[i], path + "[" + str(i) + "]")
        ]
    return [dict(path=path, before=old, after=new)]


def passes(tree):
    form = tree["m_ParsedForm"]
    require(
        form["m_Name"] == SHADER and len(form["m_SubShaders"]) == 1,
        "Unexpected original/modern shader identity or subshader count",
    )
    result = form["m_SubShaders"][0]["m_Passes"]
    require(
        len(result) == 2 and [p["m_State"]["m_Name"] for p in result] == PASS_NAMES,
        "Expected exactly the original FORWARDBASE and SHADOWCASTER passes",
    )
    return result


def patch_tree(original, modern):
    old_passes, new_passes = passes(original), passes(modern)
    for old, new in zip(old_passes, new_passes):
        require(
            "zClip" not in old["m_State"],
            "Original shader has an explicit zClip field; this repair does not apply",
        )
        value = new["m_State"].get("zClip")
        require(
            isinstance(value, dict)
            and set(value) == {"val", "name"}
            and type(value["val"]) is float
            and value["val"] == 0.0
            and value["name"] == "<noninit>",
            "Modern shader must have the exact non-property zClip Off state",
        )
    result = deepcopy(modern)
    for p in passes(result):
        p["m_State"]["zClip"]["val"] = 1.0
    return result


def expected_fields():
    return [
        dict(
            path="m_ParsedForm.m_SubShaders[0].m_Passes[" + str(i) + "].m_State.zClip.val",
            before=0.0,
            after=1.0,
        )
        for i in range(2)
    ]


def read_layout(path):
    """Read the supported wrapper without loading its ~1.6GB uncompressed data."""
    from UnityPy.helpers import CompressionHelper as compression

    with Path(path).open("rb") as stream:

        def cstring():
            result = bytearray()
            while True:
                value = stream.read(1)
                require(bool(value) and len(result) < 256, "Invalid UnityFS header string")
                if value == b"\0":
                    return result.decode("ascii")
                result.extend(value)

        signature = cstring()
        version = struct.unpack(">I", stream.read(4))[0]
        player, engine = cstring(), cstring()
        fields_at = stream.tell()
        size, compressed_size, uncompressed_size, flags = struct.unpack(">qIII", stream.read(20))
        require(
            (signature, version, player, engine, flags)
            == ("UnityFS", 8, "5.x.x", "2021.3.45f2", 579),
            "Unsupported UnityFS format; refusing a generic binary rewrite",
        )
        require(
            size == Path(path).stat().st_size
            and 0 < compressed_size < 16 * 1024 * 1024
            and 0 < uncompressed_size < 64 * 1024 * 1024,
            "Invalid bundle/header size",
        )
        stream.seek((-stream.tell()) % 16, 1)
        info_start = stream.tell()
        stream.seek(0)
        prefix = stream.read(info_start)
        packed = stream.read(compressed_size)
        info = compression.decompress_lz4(packed, uncompressed_size)
        require(
            info[:16] == b"\0" * 16,
            "Nonzero bundle content hash is unsupported; do not guess its algorithm",
        )
        count = struct.unpack_from(">i", info, 16)[0]
        require(0 < count <= 100000 and 24 + count * 10 <= len(info), "Invalid block table")
        blocks = []
        offset = 0
        for i in range(count):
            unpacked, packed_size, block_flags = struct.unpack_from(">IIH", info, 20 + i * 10)
            require(
                0 < unpacked <= 1024 * 1024
                and 0 < packed_size <= 2 * 1024 * 1024
                and block_flags == 3,
                "Only bounded LZ4HC blocks are supported",
            )
            blocks.append(
                dict(
                    uncompressed=unpacked, compressed=packed_size, flags=block_flags, offset=offset
                )
            )
            offset += unpacked
        directory_at = 20 + count * 10
        nodes_count = struct.unpack_from(">i", info, directory_at)[0]
        at = directory_at + 4
        require(1 <= nodes_count <= 100, "Invalid bundle directory count")
        nodes = []
        for _ in range(nodes_count):
            pos, length, node_flags = struct.unpack_from(">qqI", info, at)
            at += 20
            end = info.index(0, at)
            name = info[at:end].decode("utf8")
            at = end + 1
            require(
                pos >= 0 and length >= 0 and pos + length <= offset,
                "Invalid bundle directory extent",
            )
            nodes.append(dict(offset=pos, size=length, flags=node_flags, path=name))
        require(
            at == len(info) and len({n["path"] for n in nodes}) == len(nodes),
            "Unexpected/duplicate directory data",
        )
        data_start = (info_start + compressed_size + 15) // 16 * 16
        require(
            stream.read(data_start - stream.tell())
            == b"\0" * (data_start - (info_start + compressed_size)),
            "Unexpected nonzero block alignment padding",
        )
        require(
            data_start + sum(b["compressed"] for b in blocks) == size,
            "Unexpected trailing/missing compressed data",
        )
    return dict(
        prefix=prefix,
        fieldsAt=fields_at,
        info=info,
        directoryAt=directory_at,
        dataStart=data_start,
        blocks=blocks,
        nodes=nodes,
        uncompressedBytes=offset,
    )


def raw_changes(old, new):
    require(len(old) == len(new), "Shader serialization resized its payload")
    return [dict(offset=i, before=a, after=b) for i, (a, b) in enumerate(zip(old, new)) if a != b]


def rewrite_blocks(old_path, new_path, layout, changes):
    from UnityPy.helpers import CompressionHelper as compression

    require(changes and len({c["offset"] for c in changes}) == len(changes), "Invalid byte patch")
    replacements = {}
    info = bytearray(layout["info"])
    with Path(old_path).open("rb") as stream:
        stream.seek(layout["dataStart"])
        for i, block in enumerate(layout["blocks"]):
            affected = [
                c
                for c in changes
                if block["offset"] <= c["offset"] < block["offset"] + block["uncompressed"]
            ]
            if not affected:
                stream.seek(block["compressed"], 1)
                continue
            data = bytearray(
                compression.decompress_lz4(stream.read(block["compressed"]), block["uncompressed"])
            )
            for change in affected:
                at = change["offset"] - block["offset"]
                require(
                    data[at] == change["before"],
                    "Input bytes differ from admitted shader serialization",
                )
                data[at] = change["after"]
            replacements[i] = compression.compress_lz4(data)
            struct.pack_into(">I", info, 20 + i * 10 + 4, len(replacements[i]))
    require(
        sum(
            sum(
                b["offset"] <= c["offset"] < b["offset"] + b["uncompressed"]
                for b in layout["blocks"]
            )
            for c in changes
        )
        == len(changes),
        "Byte patch lies outside the bundle stream",
    )
    packed_info = compression.compress_lz4(info)
    data_start = (len(layout["prefix"]) + len(packed_info) + 15) // 16 * 16
    size = data_start + sum(
        len(replacements.get(i, b"")) if i in replacements else b["compressed"]
        for i, b in enumerate(layout["blocks"])
    )
    prefix = bytearray(layout["prefix"])
    struct.pack_into(">qIII", prefix, layout["fieldsAt"], size, len(packed_info), len(info), 579)
    with Path(old_path).open("rb") as old, Path(new_path).open("xb") as new:
        new.write(prefix)
        new.write(packed_info)
        new.write(b"\0" * (data_start - new.tell()))
        old.seek(layout["dataStart"])
        for i, block in enumerate(layout["blocks"]):
            data = old.read(block["compressed"])
            require(len(data) == block["compressed"], "Input bundle was truncated while copying")
            new.write(replacements.get(i, data))
    return sorted(replacements)


def inspect_input(input_bundle, original_shader_file):
    import UnityPy

    original_env = UnityPy.load(str(original_shader_file))
    originals = [
        o for o in original_env.objects if o.path_id == SOURCE_PID and o.type.name == "Shader"
    ]
    require(len(originals) == 1, "Exact original Shader pathID is missing/ambiguous")
    original_obj = originals[0]
    original = original_obj.read_typetree()
    require(
        original_obj.assets_file.unity_version == "5.6.7f1", "Original shader is not Unity 5.6.7f1"
    )
    origin = dict(
        path=str(Path(original_shader_file).resolve()),
        sha256=sha(original_shader_file),
        pathId=SOURCE_PID,
        shader=SHADER,
        unityVersion=original_obj.assets_file.unity_version,
        rawShaderSha256=digest(original_obj.get_raw_data()),
        passStates=[
            dict(
                name=p["m_State"]["m_Name"],
                keys=list(p["m_State"]),
                zClipAbsent="zClip" not in p["m_State"],
            )
            for p in passes(original)
        ],
    )
    env = UnityPy.load(str(input_bundle))
    targets = [
        o
        for o in env.objects
        if o.assets_file.name == CAB and o.path_id == MODERN_PID and o.type.name == "Shader"
    ]
    require(len(targets) == 1, "Exact current Shader pathID/CAB is missing/ambiguous")
    obj = targets[0]
    before = obj.read_typetree()
    patched = patch_tree(original, before)
    raw = obj.get_raw_data()
    require(
        obj.save_typetree(before) == raw,
        "UnityPy cannot round-trip this Shader byte-identically; refusing to write",
    )
    result = obj.save_typetree(patched)
    require(differences(before, patched) == expected_fields(), "Unexpected shader field changes")
    changes = raw_changes(raw, result)
    require(len(changes) == 4, "Expected exactly four changed bytes for two float0->1 state values")
    layout = read_layout(input_bundle)
    cab = [n for n in layout["nodes"] if n["path"] == CAB]
    require(
        len(cab) == 1 and obj.byte_start + len(raw) <= cab[0]["size"],
        "Shader extent is outside its serialized file",
    )
    absolute = [dict(c, offset=c["offset"] + cab[0]["offset"] + obj.byte_start) for c in changes]
    # get_raw_data reads the original reader, while save_typetree changes obj.data.
    # Restore only the in-memory object so subsequent raw comparisons are explicit.
    obj.data = None
    return env, obj, before, result, absolute, origin, layout


def verify_blocks(old_path, new_path, old_layout, new_layout, changes):
    from UnityPy.helpers import CompressionHelper as compression

    require(
        old_layout["nodes"] == new_layout["nodes"]
        and old_layout["uncompressedBytes"] == new_layout["uncompressedBytes"],
        "Bundle directory/uncompressed extents changed",
    )
    old_info, new_info = bytearray(old_layout["info"]), bytearray(new_layout["info"])
    require(
        len(old_layout["blocks"]) == len(new_layout["blocks"]), "Compressed block count changed"
    )
    changed = []
    identical = 0
    with Path(old_path).open("rb") as old, Path(new_path).open("rb") as new:
        old.seek(old_layout["dataStart"])
        new.seek(new_layout["dataStart"])
        for i, (a, b) in enumerate(zip(old_layout["blocks"], new_layout["blocks"])):
            require(
                (a["offset"], a["uncompressed"], a["flags"])
                == (b["offset"], b["uncompressed"], b["flags"]),
                "Block shape changed",
            )
            raw_a, raw_b = old.read(a["compressed"]), new.read(b["compressed"])
            expected = [
                dict(c, offset=c["offset"] - a["offset"])
                for c in changes
                if a["offset"] <= c["offset"] < a["offset"] + a["uncompressed"]
            ]
            if not expected:
                require(raw_a == raw_b, "Unchanged compressed block differs: " + str(i))
                identical += 1
            else:
                data_a = compression.decompress_lz4(raw_a, a["uncompressed"])
                data_b = compression.decompress_lz4(raw_b, b["uncompressed"])
                require(
                    raw_changes(data_a, data_b) == expected,
                    "Block contains changes beyond the two depth-clip values",
                )
                changed.append(
                    dict(
                        index=i,
                        beforeCompressedSha256=digest(raw_a),
                        afterCompressedSha256=digest(raw_b),
                    )
                )
            struct.pack_into(">I", new_info, 20 + i * 10 + 4, a["compressed"])
    require(old_info == new_info, "Bundle metadata changed beyond block compressed sizes")
    old_prefix, new_prefix = bytearray(old_layout["prefix"]), bytearray(new_layout["prefix"])
    require(len(old_prefix) == len(new_prefix), "Bundle header length changed")
    # Total file size and compressed metadata size are the only wrapper fields changed.
    at = old_layout["fieldsAt"]
    new_prefix[at : at + 12] = old_prefix[at : at + 12]
    require(old_prefix == new_prefix, "Unexpected UnityFS header change")
    return dict(
        totalBlocks=len(old_layout["blocks"]),
        unchangedCompressedBlocks=identical,
        changedBlocks=changed,
        completeDecompressedStreamDiffersOnlyAtShaderStateBytes=True,
        directoryAndResourceBytesIdentical=True,
    )


def compare_candidate(input_bundle, candidate, original_shader_file):
    import UnityPy

    old_env, target, before, expected, changes, origin, old_layout = inspect_input(
        input_bundle, original_shader_file
    )
    new_layout = read_layout(candidate)
    block_evidence = verify_blocks(input_bundle, candidate, old_layout, new_layout, changes)
    new_env = UnityPy.load(str(candidate))  # Fresh parse, never the mutated preparation object.

    def index(env):
        rows = {(o.assets_file.name, o.path_id): o for o in env.objects}
        require(len(rows) == len(env.objects), "Duplicate serialized object identity")
        return rows

    old_objects, new_objects = index(old_env), index(new_env)
    require(old_objects.keys() == new_objects.keys(), "Object identity inventory changed")
    objects = []
    unchanged = 0
    for key, a in old_objects.items():
        b = new_objects[key]
        require(
            (a.class_id, a.type_id, a.byte_start, a.byte_size)
            == (b.class_id, b.type_id, b.byte_start, b.byte_size),
            "Object metadata changed: " + repr(key),
        )
        raw_a, raw_b = a.get_raw_data(), b.get_raw_data()
        if key == (CAB, MODERN_PID):
            require(
                raw_b == expected,
                "Candidate Shader is not exactly the admitted serialized state repair",
            )
        else:
            require(raw_a == raw_b, "Non-target object payload changed: " + repr(key))
            unchanged += 1
        objects.append(
            dict(
                file=key[0],
                pathId=key[1],
                classId=a.class_id,
                bytes=a.byte_size,
                beforeSha256=digest(raw_a),
                afterSha256=digest(raw_b),
            )
        )
    after = new_objects[(CAB, MODERN_PID)].read_typetree()
    fields = differences(before, after)
    require(fields == expected_fields(), "Freshly parsed Shader has unexpected field differences")
    require(
        before["compressedBlob"] == after["compressedBlob"], "Compiled shader program blob changed"
    )
    return dict(
        passed=True,
        original=origin,
        fieldDifferences=fields,
        byteChanges=changes,
        blockVerification=block_evidence,
        shader=dict(
            name=SHADER,
            file=CAB,
            pathId=MODERN_PID,
            rawBytes=target.byte_size,
            compiledBlobBytes=len(before["compressedBlob"]),
            compiledBlobSha256=digest(before["compressedBlob"]),
            compiledProgramBytesIdentical=True,
        ),
        objectCount=len(objects),
        unchangedObjectCount=unchanged,
        objects=sorted(objects, key=lambda o: (o["file"], o["pathId"])),
    )


def prepare_bundle(input_bundle, original_shader_file, output_directory):
    import UnityPy

    input_bundle, original_shader_file, output_directory = (
        Path(p).resolve() for p in (input_bundle, original_shader_file, output_directory)
    )
    require(
        input_bundle.name == BUNDLE and input_bundle.is_file(),
        "Only the exact installed weaponry_ar15 bundle is supported",
    )
    require(
        output_directory.is_relative_to(ROOT / "build/new-engine-depth-clip")
        and not output_directory.exists(),
        "Use a fresh output directory under build/new-engine-depth-clip",
    )
    input_hash, original_hash = sha(input_bundle), sha(original_shader_file)
    env, obj, tree, raw, changes, origin, layout = inspect_input(input_bundle, original_shader_file)
    output_directory.mkdir(parents=True)
    candidate = output_directory / BUNDLE
    rewritten = rewrite_blocks(input_bundle, candidate, layout, changes)
    del env, obj, tree, raw
    gc.collect()
    evidence = compare_candidate(input_bundle, candidate, original_shader_file)
    require(
        sha(input_bundle) == input_hash and sha(original_shader_file) == original_hash,
        "Input/source changed during preparation",
    )
    proof = dict(
        schemaVersion=1,
        kind=KIND,
        passed=True,
        input=dict(path=str(input_bundle), sha256=input_hash),
        candidate=dict(path=str(candidate), sha256=sha(candidate)),
        tooling=dict(toolSha256=sha(__file__), UnityPyVersion=UnityPy.__version__),
        runtimeValidation="pending; no Editor/player launched; render-state consumption needs the same-scene image A/B",
        rewrittenBlocks=rewritten,
        verification=evidence,
    )
    proof_path = output_directory / "depth-clip-proof.json"
    with proof_path.open("x") as stream:
        json.dump(proof, stream, indent=2)
        stream.write("\n")
    return proof


def verify_candidate(input_bundle, candidate, proof_path):
    import UnityPy

    proof = json.loads(Path(proof_path).read_text())
    require(
        proof.get("schemaVersion") == 1
        and proof.get("kind") == KIND
        and proof.get("passed") is True,
        "Unsupported/failed depth-clip proof",
    )
    require(
        sha(input_bundle) == proof["input"]["sha256"]
        and sha(candidate) == proof["candidate"]["sha256"],
        "Input/candidate bundle hashes disagree with proof",
    )
    require(
        proof["tooling"] == dict(toolSha256=sha(__file__), UnityPyVersion=UnityPy.__version__),
        "Depth-clip verification tool/version changed",
    )
    origin = proof["verification"]["original"]
    require(
        sha(origin["path"]) == origin["sha256"], "Original absent-field shader provenance changed"
    )
    actual = compare_candidate(input_bundle, candidate, origin["path"])
    require(
        sha(input_bundle) == proof["input"]["sha256"]
        and sha(candidate) == proof["candidate"]["sha256"]
        and sha(origin["path"]) == origin["sha256"],
        "Bundle/source changed during independent verification",
    )
    require(actual == proof["verification"], "Independent fresh-reopen evidence differs from proof")
    require(
        [b["index"] for b in actual["blockVerification"]["changedBlocks"]]
        == proof["rewrittenBlocks"],
        "Rewritten-block evidence differs",
    )
    return dict(
        passed=True,
        inputSha256=proof["input"]["sha256"],
        candidateSha256=proof["candidate"]["sha256"],
        objectCount=actual["objectCount"],
        unchangedObjectCount=actual["unchangedObjectCount"],
        fieldDifferences=actual["fieldDifferences"],
        compiledProgramBytesIdentical=True,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="mode", required=True)
    prepare = sub.add_parser("prepare")
    prepare.add_argument("--input-bundle", type=Path, required=True)
    prepare.add_argument("--source-shaders", type=Path, default=SOURCE)
    prepare.add_argument("--output-dir", type=Path, required=True)
    verify = sub.add_parser("verify")
    verify.add_argument("--input-bundle", type=Path, required=True)
    verify.add_argument("--candidate", type=Path, required=True)
    verify.add_argument("--proof", type=Path, required=True)
    args = parser.parse_args()
    result = (
        prepare_bundle(args.input_bundle, args.source_shaders, args.output_dir)
        if args.mode == "prepare"
        else verify_candidate(args.input_bundle, args.candidate, args.proof)
    )
    print(
        json.dumps(
            {key: result[key] for key in ("passed",) if key in result} | {"mode": args.mode}
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
