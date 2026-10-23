from pathlib import Path
import json, collections
import UnityPy

root = Path(__file__).resolve().parents[1]
source = json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
lookup = {(r["copy_file"], r["copy_path_id"]): r for r in source}
rows = []


def find(value, path=""):
    result = []
    if isinstance(value, dict):
        name = value.get("name")
        while isinstance(name, dict):
            name = name.get("name")
        if "val" in value and name and name != "<noninit>":
            result.append({"field": path, "property": name, "serialized_value": value["val"]})
        for k, v in value.items():
            result += find(v, path + "." + k if path else k)
    return result


for f in sorted((root / "audit-assets/all-shader-inputs").glob("*.assets")):
    for obj in UnityPy.load(str(f)).objects:
        d = obj.read_typetree()
        r = lookup[(f.name, obj.path_id)]
        passes = []
        for si, sub in enumerate(d["m_ParsedForm"]["m_SubShaders"]):
            for pi, p in enumerate(sub["m_Passes"]):
                state = p.get("m_State", {})
                refs = find(state)
                if refs:
                    passes.append(
                        {
                            "subshader": si,
                            "pass": pi,
                            "name": state.get("m_Name"),
                            "references": refs,
                            "state": state,
                        }
                    )
        if passes:
            rows.append(
                {
                    "name": r["name"],
                    "source_file": r["source_file"],
                    "path_id": obj.path_id,
                    "passes": passes,
                }
            )
(root / "shaders/dynamic-render-state-audit.json").write_text(json.dumps(rows, indent=2))
print(
    "Shaders",
    len(rows),
    "passes",
    sum(len(r["passes"]) for r in rows),
    "refs",
    sum(len(p["references"]) for r in rows for p in r["passes"]),
)
print(collections.Counter(x["field"] for r in rows for p in r["passes"] for x in p["references"]))
