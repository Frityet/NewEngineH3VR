"""Read-only original mono stage-alternative audit for flat regenerated programs."""

from pathlib import Path
import json, re, gzip

root = Path(__file__).resolve().parents[1]
m = json.loads((root / "shaders/semantic-v2-shadow-packaged/manifest.json").read_text())
materials = {
    r["guid"]: r
    for r in json.loads((root / "shaders/material-keyword-audit.json").read_text())["shaders"]
}
selected = {}
for r in m["rows"]:
    if r["status"].startswith("candidate") and any(
        p["recovered_sets"] == 0 for p in r["pass_sets"]
    ):
        selected[(r["identity"]["copy_file"], r["identity"]["copy_path_id"])] = r


def objects():
    with gzip.open(root / "shaders/original-variants.json.gz", "rt") as f:
        next(f)
        lines = []
        for line in f:
            if line.strip() == "]" and not lines:
                return
            lines.append(line)
            if line.rstrip() in ["  },", "  }"]:
                yield json.loads("".join(lines).rstrip().rstrip(","))
                lines = []


rows = []
for original in objects():
    key = (original["copy_file"], original["path_id"])
    if key not in selected:
        continue
    r = selected[key]
    programs = original["programs"]
    stages = []
    for p in original["passes"]:
        for stage, info in p["programs"].items():
            items = [programs[s["m_BlobIndex"]] for s in info.get("m_SubPrograms", [])]
            sets = {
                tuple(sorted(i["keywords"]))
                for i in items
                if "UNITY_SINGLE_PASS_STEREO" not in i["keywords"]
            }
            if len(sets) > 1:
                stages.append(
                    {
                        "subshader": p["subshader"],
                        "pass": p["pass"],
                        "stage": stage,
                        "sets": sorted(sets),
                    }
                )
    mat = materials[r["identity"]["guid"]]
    rows.append(
        {
            "name": r["identity"]["name"],
            "guid": r["identity"]["guid"],
            "source": r["source"],
            "materials": mat["materials"],
            "material_sets": mat["sets"],
            "original_mono_alternative_stages": stages,
        }
    )
assert len(rows) == len(selected)
report = {
    "objects": rows,
    "flat_objects": len(rows),
    "objects_with_original_mono_alternatives": sum(
        bool(r["original_mono_alternative_stages"]) for r in rows
    ),
}
(root / "shaders/semantic-v2-residual-coverage.json").write_text(json.dumps(report, indent=2))
print(
    "Flat", len(rows), "with mono alternatives", report["objects_with_original_mono_alternatives"]
)
for r in rows:
    if r["original_mono_alternative_stages"]:
        print(r["name"], r["materials"], r["original_mono_alternative_stages"])
