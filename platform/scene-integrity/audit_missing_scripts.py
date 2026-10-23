#!/usr/bin/env python3
"""Compare original Steam, pristine export and working scene null components.

All inputs are read-only. Scene path IDs are retained by AssetRipper; this audit
checks that fact for every accepted baseline component and its owning object.
"""

from pathlib import Path
import argparse
import gc
import hashlib
import json
import re
import UnityPy

ROOT = Path(__file__).resolve().parents[2]


def yaml_nulls(path):
    text = path.read_text()
    objects, nulls = {}, {}
    docs = re.finditer(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n([\s\S]*?)(?=^--- !u!|\Z)", text, re.M)
    for doc in docs:
        kind, pid, body = int(doc[1]), int(doc[2]), doc[3]
        if kind == 1:
            name = re.search(r"^  m_Name: (.*)$", body, re.M)
            components = [
                int(x) for x in re.findall(r"^  - component: \{fileID: (-?\d+)\}", body, re.M)
            ]
            objects[pid] = {"name": name[1] if name else "", "components": components}
        elif kind == 114 and re.search(r"^  m_Script: \{fileID: 0\}$", body, re.M):
            nulls[pid] = int(re.search(r"^  m_GameObject: \{fileID: (-?\d+)\}", body, re.M)[1])
    return objects, nulls


def binary_nulls(path, scene_metadata=None):
    env = UnityPy.load(str(path))
    objects, nulls, mono_ids = {}, {}, set()
    for obj in env.objects:
        if obj.type.value == 29 and scene_metadata is not None:
            values = obj.read_typetree()["m_SceneGUID"]
            scene_metadata["occlusionGuid"] = "".join(
                f"{values[f'data[{i}]']:08x}"[::-1] for i in range(4)
            )
        if obj.type.value == 1:
            go = obj.read()
            objects[obj.path_id] = {
                "name": go.m_Name,
                "components": [entry.component.m_PathID for entry in go.m_Component],
            }
        elif obj.type.value == 114:
            mono_ids.add(obj.path_id)
            mb = obj.read(check_read=False)
            if mb.m_Script.m_PathID == 0:
                nulls[obj.path_id] = mb.m_GameObject.m_PathID
    return objects, nulls, mono_ids


def records(objects, nulls):
    result = []
    for go_id, go in objects.items():
        for slot, pid in enumerate(go["components"]):
            if pid == 0 or pid in nulls:
                if pid and nulls[pid] != go_id:
                    raise ValueError("Component owner mismatch: " + str(pid))
                result.append(
                    {
                        "gameObjectFileID": go_id,
                        "gameObjectName": go["name"],
                        "componentFileID": pid,
                        "componentIndex": slot,
                        "reason": "null-script" if pid else "null-component-pointer",
                    }
                )
    attached = {row["componentFileID"] for row in result}
    unattached = sorted(set(nulls) - attached)
    return sorted(
        result, key=lambda row: (row["gameObjectFileID"], row["componentIndex"])
    ), unattached


def identity(row):
    return (row["gameObjectFileID"], row["componentFileID"], row["componentIndex"], row["reason"])


def topology_hash(objects):
    payload = [(pid, objects[pid]["components"]) for pid in sorted(objects)]
    return hashlib.sha256(json.dumps(payload, separators=(",", ":")).encode()).hexdigest()


def asset_guid(path):
    return re.search(r"^guid: (\w+)$", path.with_suffix(path.suffix + ".meta").read_text(), re.M)[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=ROOT / "unity-full")
    parser.add_argument(
        "--original-data",
        type=Path,
        default=Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data",
    )
    parser.add_argument(
        "--report", type=Path, default=ROOT / "build/scene-integrity/missing-script-baseline.json"
    )
    args = parser.parse_args()
    scenes = json.loads((ROOT / "audit-assets/build-settings.json").read_text())["scenes"]
    repair_file = ROOT / "build/scene-guid-repair.json"
    repairs = {row["scene"]: row for row in json.loads(repair_file.read_text())}
    pristine_guids = {
        scene: asset_guid(ROOT / "recovered-full/ExportedProject" / scene) for scene in scenes
    }
    guid_groups = {}
    for scene in scenes:
        guid_groups.setdefault(pristine_guids[scene], []).append(scene)
    expected_repairs = {scene for group in guid_groups.values() for scene in group[1:]}
    if set(repairs) != expected_repairs:
        raise ValueError(
            "Scene GUID repair manifest does not exactly match pristine duplicate GUID groups"
        )
    results, errors = [], []
    for index, scene in enumerate(scenes):
        pristine = ROOT / "recovered-full/ExportedProject" / scene
        original = args.original_data / ("level" + str(index))
        working = args.project / scene
        pgo, pn = yaml_nulls(pristine)
        original_scene, working_scene = {}, {}
        ogo, on, original_ids = binary_nulls(original, original_scene)
        with working.open("rb") as stream:
            is_yaml = stream.read(5) == b"%YAML"
        if is_yaml:
            wgo, wn = yaml_nulls(working)
            match = re.search(r"^  m_SceneGUID: (\w+)$", working.read_text(), re.M)
            if match:
                working_scene["occlusionGuid"] = match[1]
        else:
            wgo, wn, working_ids = binary_nulls(working, working_scene)
        pristine_guid = pristine_guids[scene]
        working_guid = asset_guid(working)
        expected_guid = pristine_guid
        repair = repairs.get(scene)
        if repair:
            expected_guid = hashlib.sha256(("NativeH3VR.SceneAsset:" + scene).encode()).hexdigest()[
                :32
            ]
            if repair["oldAssetGuid"] != pristine_guid or repair["newAssetGuid"] != expected_guid:
                raise ValueError(
                    "Scene GUID repair disagrees with deterministic source algorithm: " + scene
                )
        pristine_occlusion = re.search(r"^  m_SceneGUID: (\w+)$", pristine.read_text(), re.M)
        occlusion_matches = original_scene.get("occlusionGuid") == working_scene.get(
            "occlusionGuid"
        )
        generated_empty_occlusion = (
            not original_scene
            and not pristine_occlusion
            and working_scene.get("occlusionGuid") == "0" * 32
        )
        occlusion_matches |= generated_empty_occlusion
        if pristine_occlusion:
            occlusion_matches &= pristine_occlusion[1] == original_scene.get("occlusionGuid")
        topologies = {
            "original": topology_hash(ogo),
            "pristine": topology_hash(pgo),
            "working": topology_hash(wgo),
        }
        pristine_classes = {
            int(pid): int(kind)
            for kind, pid in re.findall(r"^--- !u!(\d+) &(-?\d+)", pristine.read_text(), re.M)
        }
        topology_changes = []
        for go_id in sorted(pgo.keys() & wgo.keys()):
            before, after = pgo[go_id]["components"], wgo[go_id]["components"]
            if before == after:
                continue
            removed = [pid for pid in before if pid not in after]
            added = [pid for pid in after if pid not in before]
            only_gui_layer = (
                not added
                and all(pristine_classes[pid] == 92 for pid in removed)
                and [pid for pid in before if pid not in removed] == after
            )
            topology_changes.append(
                {
                    "gameObjectFileID": go_id,
                    "name": wgo[go_id]["name"],
                    "beforeComponents": before,
                    "afterComponents": after,
                    "removedComponents": [
                        {"fileID": pid, "classID": pristine_classes[pid]} for pid in removed
                    ],
                    "addedComponents": added,
                    "onlyRemovedLegacyGUILayer": only_gui_layer,
                }
            )
        object_ids_preserved = ogo.keys() == pgo.keys() == wgo.keys()
        explained_topology = (
            object_ids_preserved
            and topologies["original"] == topologies["pristine"]
            and all(change["onlyRemovedLegacyGUILayer"] for change in topology_changes)
        )
        pr, pu = records(pgo, pn)
        ore, ou = records(ogo, on)
        wr, wu = records(wgo, wn)
        ps, os, ws = ({identity(row) for row in rows} for rows in (pr, ore, wr))
        new = [row for row in wr if identity(row) not in os]
        lost = [row for row in ore if identity(row) not in ws]
        row = {
            "path": scene,
            "originalFile": original.name,
            "sceneGuid": expected_guid,
            "pristineSceneGuid": pristine_guid,
            "workingSceneGuidMatches": working_guid == expected_guid,
            "sceneGuidRepair": repair,
            "pristineDuplicateGuidScenes": guid_groups[pristine_guid],
            "originalOcclusionGuid": original_scene.get("occlusionGuid"),
            "workingOcclusionGuid": working_scene.get("occlusionGuid"),
            "occlusionGuidPreserved": occlusion_matches,
            "generatedEmptyOcclusionSettings": generated_empty_occlusion,
            "gameObjectTopologyHashes": topologies,
            "gameObjectTopologyPreserved": len(set(topologies.values())) == 1,
            "gameObjectIDsPreserved": object_ids_preserved,
            "componentTopologyChanges": topology_changes,
            "componentTopologyChangesExplained": explained_topology,
            "originalSha256": hashlib.sha256(original.read_bytes()).hexdigest(),
            "pristineSha256": hashlib.sha256(pristine.read_bytes()).hexdigest(),
            "workingSha256": hashlib.sha256(working.read_bytes()).hexdigest(),
            "originalNullScriptCount": len(on),
            "pristineNullScriptCount": len(pn),
            "workingNullScriptCount": len(wn),
            "expectedMissingComponentCount": len(ore),
            "originalAndPristineIdentitiesMatch": ps == os,
            "workingIdentitiesMatch": ws == os,
            "baseline": ore,
            "newMissingComponents": new,
            "removedOrMovedOriginalMissingComponents": lost,
            "unattachedNullScriptIDs": {"original": ou, "pristine": pu, "working": wu},
        }
        if (
            ps != os
            or ws != os
            or ou != pu
            or ou != wu
            or working_guid != expected_guid
            or not occlusion_matches
            or not explained_topology
        ):
            errors.append(scene)
        results.append(row)
        print(
            f"{index + 1}/{len(scenes)} original={len(ore)} pristine={len(pr)} working={len(wr)} "
            f"exact={ps == os == ws} guid={working_guid == expected_guid} "
            f"topologyExplained={explained_topology} occlusion={occlusion_matches}: {scene}",
            flush=True,
        )
        del pgo, pn, ogo, on, wgo, wn
        gc.collect()
    report = {
        "readOnly": True,
        "schemaVersion": 2,
        "sceneCount": len(results),
        "sceneGuidRepairsVerified": len(repairs),
        "sceneGuidRepairManifestSha256": hashlib.sha256(repair_file.read_bytes()).hexdigest(),
        "removedLegacyGUILayerComponents": sum(
            len(change["removedComponents"])
            for row in results
            for change in row["componentTopologyChanges"]
            if change["onlyRemovedLegacyGUILayer"]
        ),
        "originalNullScriptCount": sum(row["originalNullScriptCount"] for row in results),
        "expectedMissingComponentCount": sum(
            row["expectedMissingComponentCount"] for row in results
        ),
        "scenesWithMissingComponents": sum(
            row["expectedMissingComponentCount"] > 0 for row in results
        ),
        "errors": errors,
        "passed": not errors,
        "scenes": results,
        "inspectionCriterion": "Require exact scene path + GameObject local fileID + component slot "
        "for each runtime missing component, backed by the original component fileID in this baseline. "
        "Reject additional missing components, changed/moved baseline slots, unresolved previously "
        "non-null MonoScripts, or invalid lighting. Counts alone are insufficient. Preserve originals.",
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key != "scenes"}, indent=2))
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
