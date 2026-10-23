from pathlib import Path
import json, re, hashlib

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads((root / p).read_text())
rows = read("shaders/particle-variant-identity-map.json")
overrides = read("shaders/validated-shader-overrides.json")
staged = []
for row in rows:
    report = read(row["validation"])
    assert report["complete"] and report["passed"]
    source = root / row["validated_source"]
    targets = [(row["full_target"], row["guid"])]
    for main in read("shaders/replacement-manifest.json"):
        selected = main.get("selected")
        if (
            selected
            and selected["source_file"] == row["original_source_file"]
            and selected["source_path_id"] == row["path_id"]
        ):
            targets.append((str(Path(main["target"]).relative_to(root)), main["meta_guid"]))
    for path, guid in targets:
        target = root / path
        meta = target.with_suffix(".shader.meta")
        original = meta.read_bytes()
        assert re.search(rb"guid: (\w+)", original)[1].decode() == guid
        assert (
            re.search(r'Shader "([^"]+)"', source.read_text())[1]
            == "Particles/Vertex Lit Smooth Blended H3VR"
        )
        target.write_bytes(source.read_bytes())
        assert meta.read_bytes() == original
        staged.append(
            {
                "target": path,
                "guid": guid,
                "source": row["validated_source"],
                "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "validation": row["validation"],
                "branches": report["expected"],
            }
        )
    overrides[row["baseline_source"]] = {
        "source": row["validated_source"],
        "validation": row["validation"],
        "variant_status": "All original pass-specific keyword branches restored; native Metal draw proof.",
    }
(root / "shaders/validated-shader-overrides.json").write_text(json.dumps(overrides, indent=2))
(root / "shaders/staged-particle-variants.json").write_text(json.dumps(staged, indent=2))
bytarget = {r["target"]: r for r in staged}
full = read("shaders/staged-full-sources.json")
for row in full["shaders"]:
    if row["target"] in bytarget:
        row.update({k: bytarget[row["target"]][k] for k in ["source", "sha256", "validation"]})
(root / "shaders/staged-full-sources.json").write_text(json.dumps(full, indent=2))
main = read("shaders/working-shader-manifest.json")
for row in main:
    if row["path"] in bytarget:
        row.update(
            {
                "sha256": bytarget[row["path"]]["sha256"],
                "validation": bytarget[row["path"]]["validation"],
                "variant_status": "All original particle pass-specific branches recovered, Metal draw proof.",
            }
        )
(root / "shaders/working-shader-manifest.json").write_text(json.dumps(main, indent=2))
print("Staged", len(staged), "particle targets; all GUIDs preserved")
