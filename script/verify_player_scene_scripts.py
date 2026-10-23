#!/usr/bin/env python3
"""Read-only shipped scene script/type closure and source component topology audit.

Run with audit-assets/.venv/bin/python (UnityPy). IDs are deliberately not compared
across the build: unique structural scene mappings are proved first.
"""

from pathlib import Path
import argparse
import collections
import hashlib
import json
import re
import subprocess
import UnityPy

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for b in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(b)
    return h.hexdigest()


def stable(value):
    return hashlib.sha256(
        json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def script_bindings():
    source = json.loads((ROOT / "audit-managed/unity-preparation/rewiring.json").read_text())
    result = {}
    for row in source["bindings"]:
        key = row["guid"], row["fileID"]
        value = (
            row["assembly"].removesuffix(".dll"),
            (row["namespace"] + "." if row["namespace"] else "") + row["class_name"],
        )
        if key in result and result[key] != value:
            raise ValueError("Conflicting source MonoScript identity: " + str(key))
        result[key] = value
    return result


def external_guid(raw):
    # Unity stores each textual GUID byte with swapped hexadecimal nibbles.
    return "".join(f"{byte:02x}"[::-1] for byte in raw)


def load_scene(path, bindings=None):
    env = UnityPy.load(str(path))
    asset = next(f for f in env.assets if f.name == path.name)
    objects = asset.objects
    gameobjects, transforms, scripts, problems = {}, {}, {}, []
    for pid, obj in objects.items():
        if obj.type.name == "GameObject":
            value = obj.read()
            gameobjects[pid] = {
                "name": value.m_Name,
                "editorOnly": getattr(value, "m_TagString", None) == "EditorOnly",
                "pointers": [entry.component for entry in value.m_Component],
            }
        elif obj.type.name in ("Transform", "RectTransform"):
            value = obj.read()
            transforms[pid] = {
                "go": value.m_GameObject.m_PathID,
                "parent": value.m_Father.m_PathID,
                "children": [p.m_PathID for p in value.m_Children],
                "pose": [
                    [
                        float(getattr(v, axis)) + 0.0
                        for axis in ("x", "y", "z", "w")
                        if hasattr(v, axis)
                    ]
                    for v in (value.m_LocalPosition, value.m_LocalRotation, value.m_LocalScale)
                ],
            }
        elif obj.type.name == "MonoBehaviour":
            value = obj.read(
                check_read=False
            )  # Only the common header; script payload schemas are not needed.
            identity = None
            if value.m_Script.m_PathID:
                try:
                    pointer = value.m_Script
                    if bindings is not None:
                        if pointer.m_FileID == 0:
                            script = pointer.read()
                            identity = (
                                script.m_AssemblyName.removesuffix(".dll"),
                                (script.m_Namespace + "." if script.m_Namespace else "")
                                + script.m_ClassName,
                            )
                        else:
                            guid = external_guid(asset.externals[pointer.m_FileID - 1].guid)
                            identity = bindings[(guid, pointer.m_PathID)]
                    else:
                        script = pointer.read()
                        identity = (
                            script.m_AssemblyName.removesuffix(".dll"),
                            (script.m_Namespace + "." if script.m_Namespace else "")
                            + script.m_ClassName,
                        )
                except Exception as error:
                    problems.append(
                        f"MonoBehaviour {pid}: unresolved non-null script {value.m_Script}: {error}"
                    )
                    identity = ("UNRESOLVED", str(value.m_Script))
            scripts[pid] = {"go": value.m_GameObject.m_PathID, "identity": identity}
    for pid, go in gameobjects.items():
        go["components"] = []
        go["componentIDs"] = []
        go["occlusionAreas"] = []
        for pointer in go.pop("pointers"):
            component_id = pointer.m_PathID
            go["componentIDs"].append(component_id)
            if component_id == 0:
                go["components"].append(None)
            elif pointer.m_FileID != 0 or component_id not in objects:
                problems.append(f"GameObject {pid}: missing/external component pointer {pointer}")
                go["components"].append(("UNRESOLVED", str(pointer)))
            elif objects[component_id].type.name == "MonoBehaviour":
                item = scripts[component_id]
                if item["go"] != pid:
                    problems.append(
                        f"Component {component_id}: owner differs from GameObject {pid}"
                    )
                go["components"].append(
                    None if item["identity"] is None else ("script", *item["identity"])
                )
            else:
                go["components"].append(("native", objects[component_id].type.value))
                if objects[component_id].type.name == "OcclusionArea":
                    area = objects[component_id].read()
                    go["occlusionAreas"].append(
                        {
                            "slot": len(go["components"]) - 1,
                            "center": [
                                float(getattr(area.m_Center, axis)) + 0.0
                                for axis in ("x", "y", "z")
                            ],
                            "size": [
                                float(getattr(area.m_Size, axis)) + 0.0 for axis in ("x", "y", "z")
                            ],
                            "viewVolume": bool(area.m_IsViewVolume),
                        }
                    )
    visited, roots = set(), []

    def node(tid, excluded=False):
        if tid in visited:
            raise ValueError("Cyclic/duplicated transform child: " + str(tid))
        visited.add(tid)
        tr = transforms[tid]
        go = gameobjects[tr["go"]]
        excluded = excluded or go["editorOnly"]
        children = []
        for child_id in tr["children"]:
            if transforms[child_id]["parent"] != tid:
                raise ValueError("Child/parent transform disagreement")
            child = node(child_id, excluded)
            if child is not None:
                children.append(child)
        if excluded:
            return None
        shape = stable([go["name"], [c["shape"] for c in children]])
        return {
            "id": tr["go"],
            "name": go["name"],
            "shape": shape,
            "pose": tr["pose"],
            "children": children,
        }

    for tid, tr in transforms.items():
        if tr["parent"] == 0:
            value = node(tid)
            if value is not None:
                roots.append(value)
    if len(visited) != len(transforms):
        problems.append("Unrooted transforms remain")
    if len(transforms) != len(gameobjects) or {t["go"] for t in transforms.values()} != set(
        gameobjects
    ):
        problems.append("GameObject/Transform ownership is not one-to-one")
    return {"objects": gameobjects, "scripts": scripts, "roots": roots, "problems": problems}


def compare_scene(source, built, baseline, managed):
    errors = source["problems"] + built["problems"]
    expected_nulls = {
        (row["gameObjectFileID"], row["componentIndex"]): row["componentFileID"]
        for row in baseline["baseline"]
    }
    source_nulls = {
        (pid, slot): go["componentIDs"][slot]
        for pid, go in source["objects"].items()
        for slot, value in enumerate(go["components"])
        if value is None
    }
    if source_nulls != expected_nulls:
        errors.append(
            "Current source missing-component identities differ from the original baseline"
        )

    def root_map(scene):
        result = collections.defaultdict(list)
        for root in scene["roots"]:
            result[stable([root["name"], root["pose"], root["shape"]])].append(root)
        return result

    before, after = root_map(source), root_map(built)
    if set(before) != set(after):
        errors.append(
            f"Root hierarchy/transform identities differ: sourceOnly={len(set(before) - set(after))}, builtOnly={len(set(after) - set(before))}"
        )
    mapping, matched_nulls, used_scripts = [], [], set()

    def compare(a, b, path):
        original = source["objects"][a["id"]]
        actual = built["objects"][b["id"]]
        mapping.append([a["id"], b["id"]])
        if original["components"] != actual["components"]:
            errors.append(
                {
                    "hierarchy": path,
                    "sourceGameObject": a["id"],
                    "builtGameObject": b["id"],
                    "sourceComponents": original["components"],
                    "builtComponents": actual["components"],
                }
            )
        for slot, value in enumerate(actual["components"]):
            if value is None:
                if (a["id"], slot) not in expected_nulls:
                    errors.append(f"NEW null component at {path} slot {slot}")
                else:
                    matched_nulls.append(
                        {
                            "sourceGameObject": a["id"],
                            "builtGameObject": b["id"],
                            "componentIndex": slot,
                            "originalComponent": expected_nulls[a["id"], slot],
                        }
                    )
            elif value[0] == "script":
                used_scripts.add(tuple(value[1:]))
        if len(a["children"]) != len(b["children"]):
            errors.append("Child count changed at " + path)
        for index, (left, right) in enumerate(zip(a["children"], b["children"])):
            compare(left, right, path + "/" + str(index) + ":" + left["name"])

    for key in sorted(set(before) & set(after)):
        if len(before[key]) != 1 or len(after[key]) != 1:
            # Wurstwurld1 has two same-name, same-pose leaf roots whose native
            # OcclusionArea bounds differ. Use only these runtime fields as a
            # secondary identity; never use script/null topology to choose a
            # matching root, since that topology is what this audit verifies.
            def anchored(scene, roots):
                groups = collections.defaultdict(list)
                for root in roots:
                    areas = scene["objects"][root["id"]].get("occlusionAreas", [])
                    groups[stable(areas) if areas else None].append(root)
                return groups

            left, right = anchored(source, before[key]), anchored(built, after[key])
            if (
                len(before[key]) != len(after[key])
                or set(left) != set(right)
                or None in left
                or any(len(left[k]) != 1 or len(right[k]) != 1 for k in left)
            ):
                errors.append(
                    "Ambiguous identical root hierarchy/pose or changed OcclusionArea identity/multiplicity: "
                    + before[key][0]["name"]
                )
                continue
            for anchor in sorted(left):
                compare(
                    left[anchor][0],
                    right[anchor][0],
                    left[anchor][0]["name"] + " [OcclusionArea " + anchor[:12] + "]",
                )
            continue
        compare(before[key][0], after[key][0], before[key][0]["name"])
    for assembly, name in used_scripts:
        if assembly not in managed or name.replace("/", "+") not in managed[assembly]:
            errors.append(f"Scene MonoScript absent from shipped managed types: {assembly}:{name}")
    expected_nodes = sum(count_nodes(r) for r in source["roots"])
    built_nodes = sum(count_nodes(r) for r in built["roots"])
    if len(mapping) != expected_nodes or len(mapping) != built_nodes:
        errors.append("Not every non-EditorOnly GameObject received a unique structural mapping")
    return {
        "sourceGameObjects": len(source["objects"]),
        "builtGameObjects": len(built["objects"]),
        "mappedGameObjects": len(mapping),
        "mappingSha256": stable(mapping),
        "sourceEditorOnlyObjects": len(source["objects"]) - expected_nodes,
        "originalNullComponents": len(expected_nulls),
        "editorOnlyOriginalNulls": sum(
            pid not in {pair[0] for pair in mapping} for pid, slot in expected_nulls
        ),
        "scriptTypes": sorted([list(v) for v in used_scripts]),
        "preservedNullComponents": matched_nulls,
        "errors": errors,
        "passed": not errors,
    }


def count_nodes(root):
    return 1 + sum(count_nodes(child) for child in root["children"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player-data", type=Path, required=True)
    parser.add_argument("--project", type=Path, default=ROOT / "unity-full")
    parser.add_argument(
        "--baseline", type=Path, default=ROOT / "build/scene-integrity/missing-script-baseline.json"
    )
    parser.add_argument(
        "--report", type=Path, default=ROOT / "build/scene-integrity/player-scene-scripts.json"
    )
    parser.add_argument("--require-full", action="store_true")
    args = parser.parse_args()
    args.report.unlink(missing_ok=True)  # A failed new audit must not retain old acceptance.
    baseline = json.loads(args.baseline.read_text())
    if not baseline.get("passed") or baseline.get("errors"):
        raise ValueError("Successful original scene baseline is required")
    bypath = {row["path"].casefold(): row for row in baseline["scenes"]}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    inventory = args.report.with_name(args.report.stem + "-managed-types.json")
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(ROOT / "platform/scene-integrity/ManagedTypes.csproj"),
            "-c",
            "Release",
            "--",
            str(args.player_data / "Managed"),
            str(inventory),
        ],
        check=True,
    )
    records = json.loads(inventory.read_text())
    managed = {r["assembly"]: set(r["types"]) for r in records}
    if len(managed) != len(records):
        raise ValueError("Duplicate shipped managed assembly identities")
    env = UnityPy.load(str(args.player_data / "globalgamemanagers"))
    scenes = next(o for o in env.objects if o.type.name == "BuildSettings").read_typetree()[
        "scenes"
    ]
    if not scenes:
        raise ValueError("Player BuildSettings contains no scenes")
    if len(set(p.casefold() for p in scenes)) != len(scenes):
        raise ValueError("Duplicate player scene paths")
    if args.require_full and set(p.casefold() for p in scenes) != set(bypath):
        raise ValueError("Player scene set differs from the exact original 46-scene baseline")
    bindings = script_bindings()
    results = []
    for index, path in enumerate(scenes):
        if path.casefold() not in bypath:
            raise ValueError("Unknown shipped scene: " + path)
        expected = bypath[path.casefold()]
        source_path = args.project / expected["path"]
        built_path = args.player_data / ("level" + str(index))
        meta = Path(str(source_path) + ".meta").read_text()
        guid = re.search(r"^guid: ([0-9a-f]{32})$", meta, re.M)
        if guid is None or guid[1] != expected["sceneGuid"]:
            raise ValueError("Current scene asset GUID differs from baseline: " + str(source_path))
        source = load_scene(source_path, bindings)
        built = load_scene(built_path)
        row = compare_scene(source, built, expected, managed)
        row.update(
            scene=expected["path"],
            playerFile=built_path.name,
            sourceSha256=digest(source_path),
            playerSha256=digest(built_path),
        )
        results.append(row)
        print(
            f"{index + 1}/{len(scenes)} {path}: mapped={row['mappedGameObjects']} scripts={len(row['scriptTypes'])} originalNulls={len(row['preservedNullComponents'])} errors={len(row['errors'])}",
            flush=True,
        )
    result = {
        "schemaVersion": 1,
        "readOnly": True,
        "passed": all(r["passed"] for r in results),
        "scenes": results,
        "baselineSha256": digest(args.baseline),
        "managedInventorySha256": digest(inventory),
        "buildSettingsFileSha256": digest(args.player_data / "globalgamemanagers"),
        "sourceScriptBindingsSha256": digest(
            ROOT / "audit-managed/unity-preparation/rewiring.json"
        ),
        "limits": [
            "Player IDs are renumbered; unique root name/pose/hierarchy signatures and ordered child structure establish mapping. Colliding roots require unique matching OcclusionArea center/size/view-volume identities; remaining ambiguity fails. Native bounds preservation still requires the actual built player.",
            "This checks serialized script type presence and component topology, not CLR execution or gameplay behavior. Source EditorOnly subtrees are excluded.",
        ],
    }
    args.report.write_text(json.dumps(result, indent=2) + "\n")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
