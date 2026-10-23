#!/usr/bin/env python3
"""Explicitly classify observed migrations; never waive changed original values."""

import collections, json, re
from pathlib import Path
from serialized_payload_audit import ROOT, OLD, yaml_components, norm, diff

out = ROOT / "build/serialized-payload-audit"
plan = json.loads((ROOT / "build/video/serialization/restoration-plan.json").read_text())
staged = {
    r["target"].removeprefix("unity-full/"): ROOT / r["staged"]
    for r in plan["changed_files"]
    if r["target"].endswith(".prefab")
}
restored_ids = {
    (r["target"].removeprefix("unity-full/"), r["target_file_id"])
    for r in plan["mapping"]
    if r["target"].endswith(".prefab")
}
expected_added = {
    "m_Maskable": 1,
    "m_RaycastPadding": dict(x=0, y=0, z=0, w=0),
    "m_PixelsPerUnitMultiplier": 1,
    "m_UseSpriteMesh": 0,
    "m_Navigation.m_WrapAround": 0,
    "m_ChildScaleHeight": 0,
    "m_ChildScaleWidth": 0,
    "m_ReverseArrangement": 0,
    "m_LayoutPriority": 1,
    "m_PresetInfoIsWorld": 0,
}


def get(x, path):
    for k in path.split("."):
        x = x[k]
    return x


def equal(a, b):
    return not list(diff(a, b))


event_types = {
    "UnityEventInt",
    "Valve.VR.SteamVR_Behaviour_Pose_ConnectedChangedEvent",
    "Valve.VR.SteamVR_Behaviour_Pose_DeviceIndexChangedEvent",
    "Valve.VR.SteamVR_Behaviour_Pose_TrackingChangedEvent",
    "Valve.VR.SteamVR_Behaviour_PoseEvent",
}
counts = collections.Counter()
kinds = collections.Counter()
formats = collections.Counter()
unreviewed = []
components = fields = 0
nullscripts = 0
errors = []
with (out / "all-prefabs-classified.jsonl").open("w") as dest:
    for line in (out / "all-prefabs.jsonl").open():
        r = json.loads(line)
        formats[r["target_format"]] += 1
        source = None
        restoration = None
        for c in r["components"]:
            components += 1
            fields += c.get("source_fields", 0)
            if "error" in c or not c.get("script_matches_binding", False):
                errors.append((r["path"], c["path_id"]))
            if c.get("target_script") == ["", 0]:
                nullscripts += 1
            for d in c.get("differences", []):
                category = d["classification"]
                f = d["field"]
                after = d.get("after")
                cls = c.get("class_name") or ""
                kinds[d["kind"]] += 1
                if category != "requires-review":
                    counts[category] += 1
                    continue
                if (
                    d["kind"] == "missing"
                    and f.endswith(".m_TypeName")
                    and (
                        ("Event" in d["before"] and ("UnityEngine" in d["before"]))
                        or d["before"].split(",")[0] in event_types
                    )
                ):
                    category = "unity-event-obsolete-type-metadata"
                elif (
                    d["kind"] == "added"
                    and re.search(
                        r"\.m_PersistentCalls\.m_Calls\.\d+\.m_TargetAssemblyTypeName$", f
                    )
                    and after == ""
                ):
                    category = "unity-event-lazy-target-type-metadata"
                elif d["kind"] == "added" and (
                    cls.startswith("UnityEngine.UI.")
                    or cls == "RenderHeads.Media.AVProVideo.DisplayUGUI"
                ):
                    if f in expected_added and equal(expected_added[f], after):
                        category = "ugui-neutral-schema-addition"
                    elif f in (
                        "m_AnimationTriggers.m_SelectedTrigger",
                        "m_Colors.m_SelectedColor",
                        "m_SpriteState.m_SelectedSprite",
                    ):
                        if source is None:
                            source = yaml_components((OLD / r["path"]).read_text())
                        old = norm(source[c["path_id"]])
                        old_field = f.replace("m_Selected", "m_Highlighted")
                        if equal(get(old, old_field), after):
                            category = "ugui-selected-state-matches-original-highlighted"
                elif (
                    d["kind"] == "added"
                    and cls == "RenderHeads.Media.AVProVideo.MediaPlayer"
                    and (r["path"], c["path_id"]) in restored_ids
                ):
                    if restoration is None:
                        restoration = yaml_components(staged[r["path"]].read_text())
                    expected = norm(restoration[c["path_id"]])
                    if f in expected and equal(expected[f], after):
                        category = "validated-original-avpro-payload-restoration"
                    elif (
                        f == "_events"
                        and isinstance(expected.get(f), dict)
                        and expected[f]
                        .get("m_TypeName", "")
                        .startswith("RenderHeads.Media.AVProVideo.MediaPlayerEvent,")
                    ):
                        event_without_old_type = {
                            k: v for k, v in expected[f].items() if k != "m_TypeName"
                        }
                        if equal(event_without_old_type, after):
                            category = "validated-avpro-restoration-with-event-schema-migration"
                d["classification"] = category
                counts[category] += 1
                if category == "requires-review":
                    unreviewed.append(
                        {"path": r["path"], "path_id": c["path_id"], "class": cls, "difference": d}
                    )
        dest.write(json.dumps(r) + "\n")
summary = {
    "prefabs": sum(formats.values()),
    "formats": dict(formats),
    "components": components,
    "source_top_level_fields": fields,
    "unchanged_original_null_scripts": nullscripts,
    "component_or_script_errors": errors,
    "differences_by_classification": dict(counts),
    "remaining_review_items": len(unreviewed),
    "difference_kinds": dict(kinds),
    "changed_original_values": kinds.get("value", 0),
    "changed_original_array_lengths": kinds.get("array-length", 0),
    "float_relative_tolerance": 2e-6,
    "float_absolute_tolerance": 1e-7,
}
(out / "all-prefabs-classified-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
(out / "all-prefabs-unreviewed.json").write_text(json.dumps(unreviewed, indent=2) + "\n")
print(json.dumps(summary, indent=2))
