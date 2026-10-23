#!/usr/bin/env python3
"""Serialized use of optional SteamVR components and the actual game hand rig."""

from pathlib import Path
import re, json, subprocess, collections

root = Path(__file__).resolve().parents[1]
assets = root / "recovered-full/ExportedProject/Assets"
names = [
    "SteamVR_RenderModel",
    "SteamVR_Overlay",
    "SteamVR_Skybox",
    "SteamVR_Menu",
    "SteamVR_Ears",
    "SteamVR_LoadLevel",
    "SteamVR_Behaviour_Skeleton",
    "SteamVR_TrackedCamera",
    "ControllerButtonHints",
    "SteamVR_TrackingReferenceManager",
    "FVRViveHand",
]
guids = {}
for name in names:
    for p in (assets / "Scripts/Assembly-CSharp").rglob(name + ".cs.meta"):
        guids[re.search(r"^guid: ([a-f0-9]+)$", p.read_text(), re.M)[1]] = name
cmd = ["rg", "-l", "--glob", "*.unity", "--glob", "*.prefab", "--glob", "*.asset"]
for g in guids:
    cmd += ["-e", g]
r = subprocess.run(cmd + [str(assets)], capture_output=True, text=True)
assert r.returncode in (0, 1), r.stderr
counts = collections.Counter()
rows = []
for file in r.stdout.splitlines():
    text = Path(file).read_text()
    for block in re.split(r"^--- ", text, flags=re.M):
        m = re.search(r"m_Script: \{fileID: 11500000, guid: ([a-f0-9]+), type: 3\}", block)
        if not m or m[1] not in guids:
            continue
        name = guids[m[1]]
        counts[name] += 1
        row = {
            "path": str(Path(file).relative_to(assets)),
            "type": name,
            "enabled": re.search(r"^  m_Enabled: (\d+)$", block, re.M)[1]
            if re.search(r"^  m_Enabled: (\d+)$", block, re.M)
            else None,
        }
        if name == "FVRViveHand":
            row["indexControllerObject"] = re.search(
                r"^  Display_Controller_Index: (.*)$", block, re.M
            )[1]
        rows.append(row)
result = {
    "source": str(assets),
    "scanExtensions": [".unity", ".prefab", ".asset"],
    "guidToType": guids,
    "componentCounts": {n: counts[n] for n in names},
    "components": rows,
    "scope": "Serialized presence, not runtime reachability; dynamically created components must be audited from call sites.",
}
(root / "audit-vr/serialized-openvr-usage.json").write_text(json.dumps(result, indent=2) + "\n")
print(result["componentCounts"])
