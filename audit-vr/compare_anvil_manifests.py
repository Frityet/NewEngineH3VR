#!/usr/bin/env python3
"""Read shipped/native manifest graphs; missing native manifests remain pending."""

import pathlib
import json, hashlib
from pathlib import Path
import yaml

ROOT = Path(__file__).resolve().parents[1]
original = Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/StreamingAssets"
    )
)
native = ROOT / "build/StreamingAssets"
out = ROOT / "build/anvil-dependencies"
out.mkdir(exist_ok=True)
plan = json.loads((ROOT / "build/native-bundle-plan.json").read_text())
names = [b["name"] for b in plan["bundles"]]


def graph(folder):
    result = {}
    for name in names:
        p = folder / (name + ".manifest")
        if p.exists():
            data = yaml.safe_load(p.read_text())
            result[name] = [
                d.replace("\\", "/").split("/")[-1] for d in (data.get("Dependencies") or [])
            ]
    return result


old = graph(original)
new = graph(native)
old_edges = {(a, b) for a, bs in old.items() for b in bs}
new_edges = {(a, b) for a, bs in new.items() for b in bs}
report = {
    "original_graph": old,
    "native_graph": new,
    "native_complete": len(new) == len(names),
    "missing_native_manifests": sorted(set(names) - set(new)),
    "new_edges": [list(x) for x in sorted(new_edges - old_edges)],
    "removed_edges": [list(x) for x in sorted(old_edges - new_edges)]
    if len(new) == len(names)
    else None,
    "guaranteed_original_startup_prewarm": ["assets_resources_objectids_weaponry_ammunition"],
    "native_edges_not_covered_by_startup_prewarm": [
        list(x)
        for x in sorted(new_edges)
        if x[1] != "assets_resources_objectids_weaponry_ammunition"
    ],
}
report["manifest_sha256"] = {
    label: {
        name: hashlib.sha256((folder / (name + ".manifest")).read_bytes()).hexdigest()
        for name in names
        if (folder / (name + ".manifest")).exists()
    }
    for label, folder in [("original", original), ("native", native)]
}
report["native_plan_sha256"] = hashlib.sha256(
    (ROOT / "build/native-bundle-plan.json").read_bytes()
).hexdigest()
report["scope"] = (
    "Dependency graph only; not shader compilation, bundle content, or runtime acceptance."
)
(out / "manifest-comparison.json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
