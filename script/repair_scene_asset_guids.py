#!/usr/bin/env python3
"""Give recovered scene assets unique GUIDs without changing baked culling IDs.

AssetRipper reused internal occlusion scene GUIDs as asset GUIDs. Copied scenes in
the original game legitimately share that internal culling ID. Their .meta GUIDs
must still be unique in an editable project.
"""

from pathlib import Path
from collections import defaultdict
import argparse
import hashlib
import json
import re

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--project", type=Path, default=root / "unity-full")
args = parser.parse_args()
project = args.project.resolve()
if project.parent != root or project.name not in {"unity", "unity-full"}:
    raise SystemExit("Only generated working projects may be repaired")
groups = defaultdict(list)
for meta in (project / "Assets").rglob("*.meta"):
    match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(errors="replace"), re.M)
    if match:
        groups[match[1]].append(meta)
build_settings = project / "ProjectSettings/EditorBuildSettings.asset"
settings = build_settings.read_text()
order = re.findall(r"^    path: (.+)$", settings, re.M)
changes = []
for old, metas in groups.items():
    if len(metas) == 1:
        continue
    if any(not str(meta).endswith(".unity.meta") for meta in metas):
        raise RuntimeError("Ambiguous non-scene asset GUID: " + old)
    metas.sort(key=lambda meta: order.index(str(meta.with_suffix("").relative_to(project))))
    for meta in metas[1:]:
        path = str(meta.with_suffix("").relative_to(project))
        new = hashlib.sha256(("NativeH3VR.SceneAsset:" + path).encode()).hexdigest()[:32]
        meta.write_text(meta.read_text().replace("guid: " + old, "guid: " + new))
        settings = settings.replace(
            "    path: " + path + "\n    guid: " + old, "    path: " + path + "\n    guid: " + new
        )
        changes.append(
            {
                "scene": path,
                "oldAssetGuid": old,
                "newAssetGuid": new,
                "internalOcclusionGuid": "preserved",
            }
        )
build_settings.write_text(settings)
(root / "build/scene-guid-repair.json").write_text(json.dumps(changes, indent=2) + "\n")
print("Repaired scene asset GUIDs:", len(changes))
