from pathlib import Path
import json, hashlib, re, shutil, sys

batch = (
    "uncovered"
    if "--uncovered" in sys.argv
    else (
        "runtime"
        if "--runtime" in sys.argv
        else ("remaining" if "--remaining" in sys.argv else "residual")
    )
)
namespace = batch.capitalize() + "Variants"
root = Path(__file__).resolve().parents[1]
dest = root / f"shaders/{batch}-validated"
dest.mkdir(exist_ok=True)
files = set()
upgrades = []
allowed = {
    "glstate_matrix_projection": "UNITY_MATRIX_P",
    "glstate_matrix_modelview0": "UNITY_MATRIX_MV",
    "glstate_matrix_mvp": "UNITY_MATRIX_MVP",
}
for p in (root / f"{batch}-probe/Assets/Resources/OriginalSources").glob("*.shader"):
    shutil.copyfile(p, dest / p.name)
    files.add(dest / p.name)
    for rel in re.findall(rf'#include "(Assets/NativeShaders/{namespace}/[^\"]+)"', p.read_text()):
        source = root / f"{batch}-probe" / rel
        target = dest / rel
        if target in files:
            continue
        original = (root / f"shaders/{batch}-packaged" / rel).read_text()
        actual = source.read_text()
        if original != actual:
            notes = re.findall(
                r"^// Upgrade NOTE: replaced '([^']+)' with '([^']+)'\n", actual, re.M
            )
            expected = original
            for a, b in notes:
                assert allowed.get(a) == b
                expected = expected.replace(a, b)
            stripped = re.sub(
                r"^(?:// Upgrade NOTE: replaced '[^']+' with '[^']+'\n)+\n", "", actual
            )
            assert stripped == expected, source
            upgrades.append({"file": rel, "substitutions": notes})
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        files.add(target)
(dest / "manifest.json").write_text(
    json.dumps(
        {
            "files": [
                {
                    "path": str(p.relative_to(dest)),
                    "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
                }
                for p in sorted(files)
            ],
            "unity_matrix_spelling_upgrades": upgrades,
            "validation": f"shaders/{batch}-original-native-gpu-report.json",
        },
        indent=2,
    )
)
print("Pinned", len(files), "files, upgraded", len(upgrades))
full = json.loads((root / "build/shader-identities/full-shader-identity-map.json").read_text())
full = {(r["original_source_file"], r["path_id"]): r for r in full}
src = json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
src = {(r["copy_file"].casefold(), r["copy_path_id"]): r for r in src}
package = json.loads((root / f"shaders/{batch}-packaged/manifest.json").read_text())
equiv = {r["file"]: r.get("equivalent_to", r["file"]) for r in package["shaders"]}
rows = []
for line in (root / f"shaders/{batch}-export-identities.tsv").read_text().splitlines():
    copy, ident, path = line.split("\t")
    origin = src[(copy.casefold(), int(ident))]
    r = full[(origin["source_file"], origin["source_path_id"])]
    chosen = equiv[Path(path).name]
    rows.append(
        {
            "original_source_file": origin["source_file"],
            "path_id": origin["source_path_id"],
            "full_target": "unity-full/" + r["exported_path"],
            "guid": r["guid"],
            "baseline_source": Path(r["converted_shader_path"]).name,
            "validated_source": f"shaders/{batch}-validated/" + chosen,
        }
    )
(root / f"shaders/{batch}-variant-identity-map.json").write_text(json.dumps(rows, indent=2))
print("Mapped", len(rows), "exact identities")
