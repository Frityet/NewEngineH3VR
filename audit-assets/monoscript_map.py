#!/usr/bin/env python3
"""Map original script identities to recovered script .meta GUIDs."""

import pathlib
import json, re
from pathlib import Path
import UnityPy

here = Path(__file__).resolve().parent
root = Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
project = here.parent / "recovered/ExportedProject"
inventory = json.loads((here / "inventory.json").read_text())
items = []
for entry in inventory["files"]:
    if not entry["counts"].get("MonoScript") or entry["path"].startswith("StreamingAssets/"):
        continue
    env = UnityPy.load(str(root / entry["path"]))
    for obj in env.objects:
        if obj.type.name != "MonoScript":
            continue
        data = obj.read_typetree()
        items.append(
            dict(
                file=entry["path"],
                path_id=obj.path_id,
                assembly=data["m_AssemblyName"],
                namespace=data["m_Namespace"],
                class_name=data["m_ClassName"],
            )
        )
script_paths = list(project.glob("Assets/**/*.cs"))
for item in items:
    stem = item["assembly"].removesuffix(".dll")
    pathpart = (
        ("/".join(item["namespace"].split(".")) + "/" if item["namespace"] else "")
        + item["class_name"]
        + ".cs"
    )
    matches = [p for p in script_paths if str(p).endswith(f"/{stem}/{pathpart}")]
    item["recovered_scripts"] = []
    for match in matches:
        meta = match.with_suffix(".cs.meta")
        guids = re.findall(r"^guid: (\w+)", meta.read_text(), re.M) if meta.exists() else []
        item["recovered_scripts"].append(
            dict(path=str(match.relative_to(project)), guid=guids[0] if guids else None)
        )
(here / "monoscript-map.json").write_text(json.dumps(items, indent=2))
print(
    "Original MonoScripts",
    len(items),
    "mapped",
    sum(bool(x["recovered_scripts"]) for x in items),
    "unique identities",
    len(set((x["assembly"], x["namespace"], x["class_name"]) for x in items)),
)
