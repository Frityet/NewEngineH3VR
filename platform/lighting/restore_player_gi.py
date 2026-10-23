#!/usr/bin/env python3
"""Restore original shipped realtime GI mapping/files after Unity player build.

Does not rebake, transcode solver cores, or alter editor lighting metadata.
Requires an original scene with probeset-only Enlighten mapping. Default dry-run.
"""

from pathlib import Path
import argparse, json, hashlib, shutil, os
import UnityPy
from UnityPy.streams import EndianBinaryReader, EndianBinaryWriter
from UnityPy.helpers.TypeTreeHelper import read_value, write_value, TypeTreeConfig


def digest(b):
    return hashlib.sha256(b).hexdigest()


def settings(path):
    env = UnityPy.load(str(path))
    rows = [
        o
        for o in env.objects
        if o.assets_file.name == path.name and o.type.name == "LightmapSettings"
    ]
    if len(rows) != 1:
        raise ValueError("Expected exactly one LightmapSettings: " + str(path))
    return rows[0]


def replace_field(obj, name, value):
    raw = obj.get_raw_data()
    r = EndianBinaryReader(raw, endian=obj.reader.endian)
    config = TypeTreeConfig(True, obj.assets_file)
    for node in obj._get_typetree_node().m_Children:
        start = r.Position
        read_value(node, r, config)
        end = r.Position
        if node.m_Name != name:
            continue
        w = EndianBinaryWriter(endian=obj.reader.endian)
        write_value(value, node, w, config)
        new = w.bytes
        after = raw[:start] + new + raw[end:]
        obj.set_raw_data(after)
        assert after[:start] + after[start + len(new) :] == raw[:start] + raw[end:]
        return digest(raw[:start] + raw[end:])
    raise ValueError("Missing field " + name)


def restore(source, target, source_level, target_level, apply):
    source_obj = settings(source / source_level)
    mapping = source_obj.read_typetree()["m_EnlightenSceneMapping"]
    if any(mapping.get(k) for k in mapping if k != "m_Probesets"):
        raise ValueError(
            "Only probeset-only mapping has been validated; renderer mapping needs explicit object remap"
        )
    hashes = {bytes(h["bytes[%d]" % i] for i in range(16)).hex() for h in mapping["m_Probesets"]}
    files = {}
    for h in sorted(hashes):
        for ext in (".pos", ".rpc.sse"):
            name = h[:2] + "/" + h + ext
            p = source / "GI" / source_level / name
            if not p.is_file():
                raise ValueError("Missing original runtime file " + str(p))
            files[name] = p.read_bytes()
    expected = {
        str(p.relative_to(source / "GI" / source_level))
        for p in (source / "GI" / source_level).rglob("*")
        if p.is_file()
    }
    if set(files) != expected:
        raise ValueError("Original directory does not exactly match scene probe hashes")
    path = target / target_level
    before = path.read_bytes()
    obj = settings(path)
    old = obj.read_typetree()["m_EnlightenSceneMapping"]
    preserved = replace_field(obj, "m_EnlightenSceneMapping", mapping)
    after = obj.assets_file.save()
    check = next(o for o in UnityPy.load(after).objects if o.type.name == "LightmapSettings")
    assert check.read_typetree()["m_EnlightenSceneMapping"] == mapping
    if apply:
        if path.read_bytes() != before:
            raise RuntimeError("Target changed while preparing")
        temp = path.with_suffix(".gi-tmp")
        temp.write_bytes(after)
        os.replace(temp, path)
        for name, data in files.items():
            dst = target / "GI" / target_level / name
            dst.parent.mkdir(parents=True, exist_ok=True)
            dst.write_bytes(data)
            assert dst.read_bytes() == data
    return dict(
        sourceLevel=source_level,
        targetLevel=target_level,
        probeSets=len(hashes),
        previousProbeSets=len(old["m_Probesets"]),
        files=len(files),
        runtimeBytes=sum(map(len, files.values())),
        mappingPreservedOtherObjectBytesSHA256=preserved,
        beforeSHA256=digest(before),
        afterSHA256=digest(after),
        fileSHA256={n: digest(v) for n, v in files.items()},
    )


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument(
        "--source-data",
        type=Path,
        default=Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data",
    )
    p.add_argument("--player-data", type=Path, required=True)
    p.add_argument("--source-level")
    p.add_argument("--target-level")
    p.add_argument("--apply", action="store_true")
    p.add_argument("--report", type=Path, required=True)
    a = p.parse_args()
    if bool(a.source_level) != bool(a.target_level):
        p.error("Both level overrides required")
    if a.source_level:
        levels = [(a.source_level, a.target_level)]
    else:

        def scene_list(base):
            return next(
                o
                for o in UnityPy.load(str(base / "globalgamemanagers")).objects
                if o.type.name == "BuildSettings"
            ).read_typetree()["scenes"]

        original = scene_list(a.source_data)
        built = scene_list(a.player_data)
        levels = []
        for i, path in enumerate(built):
            matches = [j for j, s in enumerate(original) if s.casefold() == path.casefold()]
            if len(matches) != 1:
                raise ValueError("No exact unique original scene path: " + path)
            source_level = "level" + str(matches[0])
            if (a.source_data / "GI" / source_level).is_dir():
                levels.append((source_level, "level" + str(i)))
    records = [restore(a.source_data, a.player_data, s, t, a.apply) for s, t in levels]
    report = dict(
        applied=a.apply,
        playerData=str(a.player_data),
        scenes=len(records),
        probeSets=sum(r["probeSets"] for r in records),
        records=records,
    )
    a.report.parent.mkdir(parents=True, exist_ok=True)
    a.report.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({k: v for k, v in report.items() if k != "records"}))
