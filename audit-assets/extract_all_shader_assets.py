#!/usr/bin/env python3
"""Create shader-only serialized copies for old AssetRipper; original inputs read-only.
After export, use AuxiliaryFiles/path_id_map.json plus this source-map.json to
associate new shader GUIDs with source files/path IDs/names. Does not export or
modify the recovered Unity project.
"""

from pathlib import Path
import gc, json, re
import UnityPy

HERE = Path(__file__).resolve().parent
inventory = json.loads((HERE / "inventory.json").read_text())
root = Path(inventory["source"])
dest = HERE / "all-shader-inputs"
dest.mkdir(exist_ok=True)
rows = []
for index, entry in enumerate(inventory["files"]):
    if not entry["counts"].get("Shader"):
        continue
    env = UnityPy.load(str(root / entry["path"]))
    for serial_index, assetfile in enumerate(env.assets):
        objects = {k: o for k, o in assetfile.objects.items() if o.type.name == "Shader"}
        if not objects:
            continue
        stem = re.sub("[^a-zA-Z0-9_.-]", "_", entry["path"])
        target = dest / f"{index:03d}-{serial_index}-{stem}.assets"
        for obj in objects.values():
            data = obj.read_typetree()
            rows.append(
                dict(
                    source_file=entry["path"],
                    source_serialized_file=assetfile.name,
                    source_path_id=obj.path_id,
                    name=data.get("m_ParsedForm", {}).get("m_Name", data.get("m_Name")),
                    copy_file=target.name,
                    copy_path_id=obj.path_id,
                    platforms=data.get("platforms", []),
                )
            )
        assetfile.objects = objects
        target.write_bytes(assetfile.save())
        print(target.name, len(objects), flush=True)
    del env
    gc.collect()
(HERE / "all-shader-source-map.json").write_text(json.dumps(rows, indent=2))
print("Shader records", len(rows))
