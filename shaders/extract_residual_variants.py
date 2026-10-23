from pathlib import Path
import json, collections, sys
import UnityPy

root = Path(__file__).resolve().parents[1]
rows = json.loads((root / "shaders/residual-variant-coverage.json").read_text())["objects"]
names = [
    "WFX/Additive Alpha8",
    "NatureManufacture Shaders/Cross Model Shader",
    "BulletHole",
    "Hidden/SimpleAdd",
]
batch = (
    "uncovered"
    if "--uncovered" in sys.argv
    else (
        "runtime"
        if "--runtime" in sys.argv
        else ("remaining" if "--remaining" in sys.argv else "residual")
    )
)
if batch == "remaining":
    names = [
        r["name"]
        for r in json.loads((root / "shaders/residual-variant-coverage.json").read_text())[
            "families"
        ]
        if r["materials"]
        and r["mono_alternatives"]
        and r["name"] not in names
        and r["name"] != "CynCliff HeightBlend"
    ]
if batch == "runtime":
    names = [
        r["name"]
        for r in json.loads((root / "shaders/residual-variant-coverage.json").read_text())[
            "families"
        ]
        if r["original_managed_literal_references"] and r["mono_alternatives"]
    ]
wanted = None
if batch == "uncovered":
    runtime = set(
        json.loads((root / "shaders/runtime-recovery-selection.json").read_text())["names"]
    )
    target_rows = [
        r for r in rows if r["has_original_mono_alternatives"] and r["name"] not in runtime
    ]
    names = sorted({r["name"] for r in target_rows})
    wanted = {(r["original_source_file"], r["path_id"]) for r in target_rows}
selection = root / f"shaders/{batch}-recovery-selection.json"
if selection.exists():
    frozen = json.loads(selection.read_text())
    names = frozen[
        "names"
    ]  # Preserve the exact selection after staging changes the coverage audit.
    if batch == "uncovered":
        wanted = {(r["source_file"], r["source_path_id"]) for r in frozen["objects"]}
source = json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
byfile = collections.defaultdict(set)
selected = []
for r in source:
    if r["name"] in names and (wanted is None or (r["source_file"], r["source_path_id"]) in wanted):
        byfile[r["copy_file"]].add(r["copy_path_id"])
        selected.append(r)
dest = root / f"shaders/{batch}-variant-inputs"
dest.mkdir(exist_ok=True)
for name, ids in byfile.items():
    env = UnityPy.load(str(root / "audit-assets/all-shader-inputs" / name))
    for asset in env.assets:
        asset.objects = {k: o for k, o in asset.objects.items() if o.path_id in ids}
        if asset.objects:
            (dest / name).write_bytes(asset.save())
(root / f"shaders/{batch}-recovery-selection.json").write_text(
    json.dumps({"names": names, "objects": selected}, indent=2)
)
print("Selected", len(selected), "objects", len(byfile), "files")
