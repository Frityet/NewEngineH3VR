#!/usr/bin/env python3
"""Restore missing LightingDataAsset scene metadata without touching baked data.

Default is read-only. --apply changes only the m_Scene PPtr in each linked
LightingDataAsset, using its owner's CURRENT scene .meta GUID. Internal scene
occlusion IDs and every baked payload byte remain unchanged.
"""

from pathlib import Path
import argparse, hashlib, json, re, struct


def link_binary(data, guid):
    import UnityPy
    from UnityPy.files.SerializedFile import FileIdentifier

    env = UnityPy.load(data)
    objects = list(env.objects)
    if len(objects) != 1 or objects[0].type.value != 1120 or objects[0].path_id != 112000000:
        raise ValueError("Expected one LightingDataAsset in binary file")
    obj = objects[0]
    tree = obj.read_typetree()
    original = dict(tree)
    original.pop("m_Scene", None)
    # Unity GUID text writes low nibble first for each serialized byte.
    raw_guid = bytes.fromhex("".join(guid[i + 1] + guid[i] for i in range(0, 32, 2)))
    externals = obj.assets_file.externals
    index = next((i for i, e in enumerate(externals) if e.guid == raw_guid and e.type == 3), None)
    if index is None:
        entry = object.__new__(FileIdentifier)
        entry.guid = raw_guid
        entry.type = 3
        entry.path = ""
        entry.temp_empty = ""
        index = len(externals)
        externals.append(entry)
    pointer = {"m_FileID": index + 1, "m_PathID": 102900000}
    if tree.get("m_Scene") == pointer:
        return data, original
    # Patch only the twelve pointer bytes in object data. Rewriting the whole
    # typetree could normalize legacy NaN bit patterns in baked renderer data.
    raw = obj.get_raw_data()
    offset = 0
    for node in obj.serialized_type.node.m_Children:
        if node.m_Name == "m_Scene":
            if node.m_ByteSize != 12:
                raise ValueError("Unexpected SceneAsset pointer size")
            break
        if node.m_Name == "m_Name" and node.m_Type == "string":
            length = struct.unpack_from(obj.reader.endian + "i", raw, offset)[0]
            offset = (offset + 4 + length + 3) & ~3
        elif node.m_ByteSize >= 0:
            offset += node.m_ByteSize
        else:
            raise ValueError("Unexpected field before binary scene pointer")
    else:
        raise ValueError("No binary scene field")
    patched = (
        raw[:offset]
        + struct.pack(obj.reader.endian + "iq", index + 1, 102900000)
        + raw[offset + 12 :]
    )
    obj.set_raw_data(patched)
    result = obj.assets_file.save()
    check_obj = next(iter(UnityPy.load(result).objects))
    if check_obj.get_raw_data() != patched or check_obj.read_typetree()["m_Scene"] != pointer:
        raise AssertionError("Binary scene pointer failed to round trip")
    if patched[:offset] + patched[offset + 12 :] != raw[:offset] + raw[offset + 12 :]:
        raise AssertionError("Baked binary payload changed")
    return result, original


def plan(project):
    project = Path(project).resolve()
    guid_paths = {}
    for meta in (project / "Assets/Scenes").rglob("*.asset.meta"):
        match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(), re.M)
        if match:
            if match[1] in guid_paths:
                raise ValueError("Duplicate asset GUID: " + match[1])
            guid_paths[match[1]] = meta.with_suffix("")
    records, edits, owners = [], [], set()
    for scene in sorted((project / "Assets/Scenes").rglob("*.unity")):
        match = re.search(
            r"^  m_LightingDataAsset: \{fileID: 112000000, guid: ([0-9a-f]{32}), type: 2\}$",
            scene.read_text(),
            re.M,
        )
        if not match:
            continue
        lighting = guid_paths[match[1]]
        if lighting in owners:
            raise ValueError("Lighting data shared by multiple scenes: " + str(lighting))
        owners.add(lighting)
        guid = re.search(
            r"^guid: ([0-9a-f]{32})$", scene.with_suffix(".unity.meta").read_text(), re.M
        )[1]
        before = lighting.read_bytes()
        if not before.startswith(b"%YAML"):
            after, original = link_binary(before, guid)
            payload_hash = hashlib.sha256(
                json.dumps(original, sort_keys=True, default=lambda x: bytes(x).hex()).encode()
            ).hexdigest()
            records.append(
                {
                    "scene": str(scene.relative_to(project)),
                    "sceneGuid": guid,
                    "lighting": str(lighting.relative_to(project)),
                    "lightingGuid": match[1],
                    "changed": before != after,
                    "format": "binary",
                    "preservedPayloadSHA256": payload_hash,
                }
            )
            edits.append((lighting, before, after))
            continue
        if b"--- !u!1120 &112000000\nLightingDataAsset:" not in before:
            raise ValueError("Expected original text LightingDataAsset: " + str(lighting))
        field = b"  m_Scene: {fileID: 102900000, guid: " + guid.encode() + b", type: 3}\n"
        pattern = rb"^  m_Scene: [^\n]*\n"
        if re.search(pattern, before, re.M):
            after = re.sub(pattern, field, before, count=1, flags=re.M)
        else:
            offset = before.index(b"  m_Lightmaps:")
            after = before[:offset] + field + before[offset:]
        original_payload = re.sub(pattern, b"", before, flags=re.M)
        resulting_payload = re.sub(pattern, b"", after, flags=re.M)
        if original_payload != resulting_payload:
            raise AssertionError("Unexpected baked payload change")
        records.append(
            {
                "scene": str(scene.relative_to(project)),
                "sceneGuid": guid,
                "lighting": str(lighting.relative_to(project)),
                "lightingGuid": match[1],
                "changed": before != after,
                "preservedPayloadSHA256": hashlib.sha256(original_payload).hexdigest(),
            }
        )
        edits.append((lighting, before, after))
    return records, edits


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    records, edits = plan(args.project)
    if args.apply:
        for path, before, after in edits:
            if path.read_bytes() != before:
                raise RuntimeError("Lighting changed since planning: " + str(path))
            if before != after:
                path.write_bytes(after)
    result = {
        "project": str(args.project.resolve()),
        "applied": args.apply,
        "linkedScenes": len(records),
        "changed": sum(r["changed"] for r in records),
        "records": records,
    }
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2) + "\n")
    print(
        "Lighting scene links:", len(records), "changes:", result["changed"], "applied:", args.apply
    )
