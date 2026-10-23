"""Name proven original optic passes so exact build-time filtering can identify them.

Run only at the parent build's authorized idle checkpoint. No shader program or
render-state change is permitted by this staging operation.
"""

from pathlib import Path
import sys, json, re, hashlib, shutil

if "--apply" not in sys.argv:
    raise SystemExit("Requires --apply at an authorized idle checkpoint")
from verify_shader_proof_logs import require_clean

require_clean(
    Path(__file__).resolve().parent / "unity2021-optics-filter-build.log",
    Path(__file__).resolve().parent / "optics-filter-native-player.log",
    Path(__file__).resolve().parent / "unity2021-generic-filter-particle-runtime-build.log",
    Path(__file__).resolve().parent / "generic-filter-particle-native-player.log",
)
root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads((root / p).read_text())
proof = read("shaders/optics-filter-native-gpu-report.json")
assert proof["passed"] and proof["draws"] == 510
rows = read("shaders/variant-pass-identities/manifest.json")
identities = {
    r["exported_path"]: r for r in read("build/shader-identities/full-shader-identity-map.json")
}
overrides = read("shaders/validated-shader-overrides.json")
staged = []
for row in rows:
    source = root / row["new_source"]
    content = source.read_text()
    # The only source edit is a stable name added to each unnamed pass.
    original = (root / row["source"]).read_text()
    assert re.sub(r'\s*Name "NATIVE_RECOVERED_PASS_\d+"', "", content) == original
    targets = [(row["target"], row["guid"])]
    for main in read("shaders/replacement-manifest.json"):
        chosen = main.get("selected")
        if (
            chosen
            and chosen["source_file"] == row["original_source_file"]
            and chosen["source_path_id"] == row["path_id"]
        ):
            targets.append((str(Path(main["target"]).relative_to(root)), main["meta_guid"]))
    for path, guid in targets:
        target = root / path
        meta = target.with_suffix(".shader.meta")
        before = meta.read_bytes()
        assert re.search(rb"guid: (\w+)", before)[1].decode() == guid
        assert re.search(r'Shader "([^"]+)"', target.read_text())[1] == row["name"]
        target.write_bytes(source.read_bytes())
        assert meta.read_bytes() == before
        staged.append(
            dict(
                target=path,
                guid=guid,
                source=row["new_source"],
                sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                validation="shaders/optics-filter-native-gpu-report.json",
            )
        )
    identity = identities[str(Path(row["target"]).relative_to("unity-full"))]
    overrides[Path(identity["converted_shader_path"]).name] = dict(
        source=row["new_source"],
        validation="shaders/optics-filter-native-gpu-report.json",
        variant_status="Original paired branches and pass order/state preserved; stable pass names permit exact synthetic-combination filtering.",
    )

for project, helper in [
    ("unity", "Assets/Editor/NativeOriginalVariantFilter.cs"),
    ("unity-full", "Assets/Editor/NativeBuildTools/NativeOriginalVariantFilter.cs"),
]:
    shutil.copyfile(root / "shaders/NativeOriginalVariantFilter.cs", root / project / helper)
(root / "shaders/validated-shader-overrides.json").write_text(json.dumps(overrides, indent=2))
(root / "shaders/staged-variant-pass-identities.json").write_text(json.dumps(staged, indent=2))
bytarget = {r["target"]: r for r in staged}
full = read("shaders/staged-full-sources.json")
for row in full["shaders"]:
    if row["target"] in bytarget:
        row.update({k: bytarget[row["target"]][k] for k in ["source", "sha256", "validation"]})
(root / "shaders/staged-full-sources.json").write_text(json.dumps(full, indent=2))
main = read("shaders/working-shader-manifest.json")
for row in main:
    if row["path"] in bytarget:
        row.update({k: bytarget[row["path"]][k] for k in ["sha256", "validation"]})
(root / "shaders/working-shader-manifest.json").write_text(json.dumps(main, indent=2))
print("Staged", len(staged), "optic pass-name-only changes; GUIDs preserved.")
