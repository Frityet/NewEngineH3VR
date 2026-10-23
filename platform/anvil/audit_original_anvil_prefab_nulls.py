#!/usr/bin/env python3
"""Audit active AnvilPrefabSpawn null Prefab references against original Steam levels.

This is a read-only evidence collector. It scans the pristine AssetRipper scene
exports, then checks each exact GameObject/component identity in its original
Unity 5.6 level binary using UnityPy. It never edits either source tree.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess

import UnityPy


ROOT = Path(__file__).resolve().parents[2]
SCRIPT_GUID = "1f4bcad098bbdd4cad7d6da304e9e395"
TYPE_NAME = "AnvilPrefabSpawn"
DEFAULT_DATA = Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
DEFAULT_RECOVERED = ROOT / "recovered-full/ExportedProject"
DEFAULT_BASELINE = ROOT / "build/scene-integrity/missing-script-baseline.json"
DEFAULT_MANAGED = DEFAULT_DATA / "Managed/Assembly-CSharp.dll"
DEFAULT_SOURCE = DEFAULT_RECOVERED / "Assets/Scripts/Assembly-CSharp/Anvil/AnvilPrefabSpawn.cs"
DEFAULT_MONOSCRIPT_MAP = ROOT / "audit-assets/monoscript-map.json"
DEFAULT_ILSPY = Path.home() / ".dotnet/tools/ilspycmd"


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def scene_documents(text: str):
    return re.split(r"(?=^--- !u!\d+ &)", text, flags=re.M)


def null_prefab_components(scene_path: Path):
    text = scene_path.read_text(errors="replace")
    docs = scene_documents(text)
    game_objects = {}
    for document in docs:
        header = re.match(r"--- !u!1 &(\d+)\s", document)
        if not header:
            continue
        name = re.search(r"^  m_Name: (.*)$", document, re.M)
        components = re.findall(r"^  - component: \{fileID: (\d+)\}$", document, re.M)
        game_objects[int(header[1])] = {
            "name": name[1] if name else "",
            "components": [int(value) for value in components],
        }

    found = []
    for document in docs:
        header = re.match(r"--- !u!114 &(\d+)\s", document)
        if not header or not re.search(
            rf"^  m_Script: \{{fileID: 11500000, guid: {SCRIPT_GUID}, type: 3\}}$", document, re.M
        ):
            continue
        prefab = re.search(
            r"^  Prefab: \{fileID: (\d+)(?:, guid: ([0-9a-f]+), type: (\d+))?\}$", document, re.M
        )
        if not prefab or int(prefab[1]) != 0:
            continue
        game_object = re.search(r"^  m_GameObject: \{fileID: (\d+)\}$", document, re.M)
        enabled = re.search(r"^  m_Enabled: (\d+)$", document, re.M)
        if not game_object:
            raise ValueError(f"Anvil component has no GameObject reference in {scene_path}")
        go_id = int(game_object[1])
        owner = game_objects.get(go_id, {})
        found.append(
            {
                "componentFileID": int(header[1]),
                "gameObjectFileID": go_id,
                "gameObjectName": owner.get("name", ""),
                "activeComponents": owner.get("components", []),
                "componentIndex": owner.get("components", []).index(int(header[1]))
                if int(header[1]) in owner.get("components", [])
                else None,
                "componentEnabled": int(enabled[1]) if enabled else None,
                "prefabYamlPPtr": {
                    "fileID": int(prefab[1]),
                    "guid": prefab[2],
                    "type": int(prefab[3]) if prefab[3] else None,
                },
            }
        )
    return found


def read_pptr(raw: bytes, offset: int):
    if len(raw) < offset + 12:
        raise ValueError(f"Truncated PPtr at byte {offset} in {len(raw)}-byte MonoBehaviour")
    return {
        "fileID": struct.unpack_from("<i", raw, offset)[0],
        "pathID": struct.unpack_from("<q", raw, offset + 4)[0],
        "rawOffset": offset,
    }


def mono_script_id_offset(raw: bytes):
    """Return Prefab/tm PPtr offsets after MonoBehaviour's standard fields."""
    if len(raw) < 32:
        raise ValueError("MonoBehaviour is too short for its standard header and name")
    name_length = struct.unpack_from("<i", raw, 28)[0]
    if name_length < 0 or 32 + name_length > len(raw):
        raise ValueError("Invalid MonoBehaviour m_Name length")
    return (32 + name_length + 3) & ~3


def original_component(data_root: Path, level_name: str, candidate: dict, script_path_id: int):
    level = data_root / level_name
    env = UnityPy.load(str(level))
    objects = {(obj.type.name, obj.path_id): obj for obj in env.objects}
    game_object_id = candidate["gameObjectFileID"]
    component_id = candidate["componentFileID"]
    game_object_obj = objects.get(("GameObject", game_object_id))
    if game_object_obj is None:
        raise ValueError(f"Original {level_name} lacks GameObject pathID {game_object_id}")
    game_object = game_object_obj.read()
    if game_object.m_Name != candidate["gameObjectName"]:
        raise ValueError(f"Original GameObject name mismatch at {level_name}:{game_object_id}")
    attached = {component.path_id for component in game_object.m_Components}
    if component_id not in attached:
        raise ValueError(f"Original GameObject lacks component pathID {component_id}")
    component_obj = objects.get(("MonoBehaviour", component_id))
    if component_obj is None:
        raise ValueError(f"Original MonoBehaviour pathID {component_id} is unresolved")
    if component_obj.type.name != "MonoBehaviour":
        raise ValueError(f"Original component is {component_obj.type.name}, not MonoBehaviour")
    tree = component_obj.read_typetree(check_read=False)
    if tree.get("m_Script", {}).get("m_PathID") != script_path_id:
        raise ValueError(f"Original component has unexpected script pointer {tree.get('m_Script')}")
    raw = component_obj.get_raw_data()
    prefab_offset = mono_script_id_offset(raw)
    prefab = read_pptr(raw, prefab_offset)
    tm = read_pptr(raw, prefab_offset + 12)

    transform_id = next(
        (
            item.path_id
            for item in game_object.m_Components
            if objects.get(("Transform", item.path_id)) is not None
        ),
        None,
    )
    transform = objects.get(("Transform", transform_id)) if transform_id is not None else None
    parent_id = transform.read().m_Father.path_id if transform is not None else None
    component_index = next(
        index for index, item in enumerate(game_object.m_Components) if item.path_id == component_id
    )
    hierarchy = []
    cursor = transform_id
    while cursor:
        current_transform = objects.get(("Transform", cursor))
        if current_transform is None:
            raise ValueError(f"Broken Transform parent chain at pathID {cursor}")
        transform_data = current_transform.read()
        current_go_ref = transform_data.m_GameObject
        current_go = objects.get(("GameObject", current_go_ref.path_id))
        if current_go is None:
            raise ValueError(f"Broken GameObject reference at pathID {current_go_ref.path_id}")
        current_go_data = current_go.read()
        hierarchy.append(
            {
                "transformPathID": cursor,
                "gameObjectPathID": current_go_ref.path_id,
                "gameObjectName": current_go_data.m_Name,
                "activeSelf": bool(current_go_data.m_IsActive),
                "parentTransformPathID": transform_data.m_Father.path_id,
            }
        )
        cursor = transform_data.m_Father.path_id
    return {
        "levelFile": str(level),
        "levelSha256": sha256_file(level),
        "gameObjectPathID": game_object_id,
        "gameObjectName": game_object.m_Name,
        "gameObjectActiveSelf": bool(game_object.m_IsActive),
        "transformPathID": transform_id,
        "transformParentPathID": parent_id,
        "transformHierarchyFromObjectToRoot": hierarchy,
        "gameObjectActiveInHierarchy": all(row["activeSelf"] for row in hierarchy),
        "componentPathID": component_id,
        "componentIndex": component_index,
        "componentByteStart": component_obj.byte_start,
        "componentByteSize": component_obj.byte_size,
        "componentEnabled": int(tree.get("m_Enabled", 0)),
        "scriptPPtr": tree.get("m_Script"),
        "scriptPathIDExpected": script_path_id,
        "componentRawHex": raw.hex(),
        "componentRawBytes": len(raw),
        "componentRawSha256": sha256_bytes(raw),
        "m_NameByteLength": struct.unpack_from("<i", raw, 28)[0],
        "prefabPPtr": prefab,
        "tmPPtr": tm,
        "prefabPPtrIsNull": prefab["fileID"] == 0 and prefab["pathID"] == 0,
    }


def decompile_awake(ilspy: Path, managed: Path, source_path: Path):
    env = dict(os.environ)
    env["DOTNET_ROOT"] = "/opt/homebrew/opt/dotnet/libexec"
    result = subprocess.run(
        [str(ilspy), "-il", "-t", "Anvil.AnvilPrefabSpawn", str(managed)],
        check=True,
        capture_output=True,
        text=True,
        env=env,
    )
    text = result.stdout
    marker = "} // end of method AnvilPrefabSpawn::Awake"
    end = text.find(marker)
    if end < 0:
        raise ValueError("Could not isolate AnvilPrefabSpawn.Awake IL body")
    start = text.rfind("\t.method", 0, end)
    if start < 0 or "instance void Awake () cil managed" not in text[start:end]:
        raise ValueError("Could not locate exact AnvilPrefabSpawn.Awake declaration")
    body = text[start : end + len(marker)]
    source_text = source_path.read_text(errors="replace")
    source_start = source_text.find("\t\tprivate void Awake()")
    if source_start < 0:
        raise ValueError("Recovered source has no Awake implementation")
    source_end = source_text.find("\n\t\tprivate void SpawnChild", source_start)
    semantic_excerpt = source_text[source_start:source_end].strip()
    return {
        "toolVersion": subprocess.run(
            [str(ilspy), "--version"], check=True, capture_output=True, text=True, env=env
        ).stdout.strip(),
        "awakeIl": body,
        "awakeIlSha256": sha256_bytes(body.encode()),
        "awakeSemanticExcerpt": semantic_excerpt,
        "awakeSemanticExcerptSha256": sha256_bytes(semantic_excerpt.encode()),
        "awakeDereferenceEvidence": "IL_000b branches into play path; IL_000c loads Prefab; IL_0011 calls AnvilAsset.GetGameObjectAsync().",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-data", type=Path, default=DEFAULT_DATA)
    parser.add_argument("--recovered-assets", type=Path, default=DEFAULT_RECOVERED)
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--managed", type=Path, default=DEFAULT_MANAGED)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--monoscript-map", type=Path, default=DEFAULT_MONOSCRIPT_MAP)
    parser.add_argument("--ilspy", type=Path, default=DEFAULT_ILSPY)
    parser.add_argument(
        "--report",
        type=Path,
        default=ROOT / "build/anvil-original-defects/anvil-prefabspawn-null-audit.json",
    )
    args = parser.parse_args()

    baseline = json.loads(args.baseline.read_text())
    entries = []
    script_path_ids = set()
    script_map_record = None
    mono_map = json.loads(args.monoscript_map.read_text())
    for row in mono_map:
        if (
            row.get("class_name") == TYPE_NAME
            and row.get("namespace") == "Anvil"
            and any(
                source.get("path", "").endswith("/Anvil/AnvilPrefabSpawn.cs")
                for source in row.get("recovered_scripts", [])
            )
        ):
            script_path_ids.add(int(row["path_id"]))
            script_map_record = row
    if len(script_path_ids) != 1:
        raise ValueError(
            f"Expected one original AnvilPrefabSpawn script pathID, found {sorted(script_path_ids)}"
        )
    script_path_id = next(iter(script_path_ids))

    original_global = args.game_data / "globalgamemanagers.assets"
    global_env = UnityPy.load(str(original_global))
    mono_script = next(
        (
            obj
            for obj in global_env.objects
            if obj.type.name == "MonoScript" and obj.path_id == script_path_id
        ),
        None,
    )
    if mono_script is None:
        raise ValueError(f"Original globalgamemanagers lacks MonoScript pathID {script_path_id}")
    mono_script_tree = mono_script.read_typetree(check_read=False)
    if (
        mono_script_tree.get("m_ClassName") != TYPE_NAME
        or mono_script_tree.get("m_Namespace") != "Anvil"
        or mono_script_tree.get("m_AssemblyName") != "Assembly-CSharp.dll"
    ):
        raise ValueError(f"Unexpected original MonoScript metadata: {mono_script_tree}")

    source_scene_paths = []
    for scene in baseline["scenes"]:
        scene_path = args.recovered_assets / scene["path"]
        if not scene_path.is_file():
            raise FileNotFoundError(scene_path)
        source_scene_paths.append(scene_path)
        for candidate in null_prefab_components(scene_path):
            original = original_component(
                args.game_data, scene["originalFile"], candidate, script_path_id
            )
            entries.append(
                {
                    "scene": scene["path"],
                    "originalLevel": scene["originalFile"],
                    "pristineYamlSha256": sha256_file(scene_path),
                    "scriptGuid": SCRIPT_GUID,
                    **candidate,
                    "originalBinary": original,
                    "yamlBinaryIdentityMatches": original["gameObjectPathID"]
                    == candidate["gameObjectFileID"]
                    and original["componentPathID"] == candidate["componentFileID"]
                    and original["componentIndex"] == candidate["componentIndex"]
                    and original["gameObjectName"] == candidate["gameObjectName"]
                    and original["componentEnabled"] == candidate["componentEnabled"],
                    "yamlBinaryPrefabNullMatches": original["prefabPPtrIsNull"]
                    and candidate["prefabYamlPPtr"]["fileID"] == 0,
                }
            )

    il_evidence = decompile_awake(args.ilspy, args.managed, args.source)
    report = {
        "schemaVersion": 1,
        "readOnly": True,
        "passed": all(
            row["yamlBinaryIdentityMatches"] and row["yamlBinaryPrefabNullMatches"]
            for row in entries
        ),
        "sceneCountScanned": len(source_scene_paths),
        "sceneSourceFiles": [
            {"path": str(path), "sha256": sha256_file(path)} for path in source_scene_paths
        ],
        "type": "Anvil.AnvilPrefabSpawn",
        "scriptGuid": SCRIPT_GUID,
        "scriptPathID": script_path_id,
        "scriptMapRecord": script_map_record,
        "scriptMapPath": str(args.monoscript_map),
        "scriptMapSha256": sha256_file(args.monoscript_map),
        "originalGlobalGameManagersAssets": str(original_global),
        "originalGlobalGameManagersAssetsSha256": sha256_file(original_global),
        "originalMonoScriptMetadata": mono_script_tree,
        "managedAssembly": str(args.managed),
        "managedAssemblySha256": sha256_file(args.managed),
        "sourceExcerptPath": str(args.source),
        "sourceExcerptFileSha256": sha256_file(args.source),
        "awake": il_evidence,
        "candidateNullPrefabCount": len(entries),
        "candidateNullPrefabs": entries,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    print(
        json.dumps(
            {
                "report": str(args.report),
                "passed": report["passed"],
                "sceneCountScanned": report["sceneCountScanned"],
                "candidateNullPrefabCount": report["candidateNullPrefabCount"],
            },
            indent=2,
        )
    )
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
