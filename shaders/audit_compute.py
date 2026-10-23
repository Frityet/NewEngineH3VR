import pathlib
from pathlib import Path
import json, hashlib, UnityPy

root = Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
out = Path(__file__).resolve().parent / "compute-original"
out.mkdir(exist_ok=True)
rows = []
for obj in UnityPy.load(str(root / "sharedassets0.assets")).objects:
    if obj.type.name != "ComputeShader":
        continue
    d = obj.read_typetree()
    name = d.get("m_Name")
    (out / f"{obj.path_id}-{name}.json").write_text(json.dumps(d, indent=2))
    rows.append(
        {
            "name": name,
            "path_id": obj.path_id,
            "raw_sha256": hashlib.sha256(obj.get_raw_data()).hexdigest(),
            "fields": list(d),
        }
    )
(out / "manifest.json").write_text(json.dumps(rows, indent=2))
print(rows)
