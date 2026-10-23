"""Install exact native-tested custom runtime shaders at a coordinated idle checkpoint.
Installs helper only; caller must invoke NativeOriginalVariantFilter.Configure before build.
"""

import sys

if "--apply" not in sys.argv:
    raise SystemExit("Run with --apply only at an authorized idle checkpoint")
from pathlib import Path
import json, re, hashlib, shutil
from verify_shader_proof_logs import require_clean

require_clean(
    Path(__file__).resolve().parent / "unity2021-runtime-build.log",
    Path(__file__).resolve().parent / "runtime-original-native-player.log",
)
root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads((root / p).read_text())
proof = read("shaders/runtime-original-native-gpu-report.json")
assert proof["passed"] and proof["draws"] == 3048
snapshot = root / "shaders/runtime-validated"
for r in read("shaders/runtime-validated/manifest.json")["files"]:
    assert hashlib.sha256((snapshot / r["path"]).read_bytes()).hexdigest() == r["sha256"]
rows = read("shaders/runtime-variant-identity-map.json")
overrides = read("shaders/validated-shader-overrides.json")
staged = []
for row in rows:
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
            == re.search(r'Shader "([^"]+)"', target.read_text())[1]
        )
        target.write_bytes(source.read_bytes())
        assert meta.read_bytes() == original
        staged.append(
            {
                "target": path,
                "guid": guid,
                "source": row["validated_source"],
                "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "validation": "shaders/runtime-original-native-gpu-report.json",
            }
        )
    overrides[row["baseline_source"]] = {
        "source": row["validated_source"],
        "validation": "shaders/runtime-original-native-gpu-report.json",
        "variant_status": "All original paired branches preserved in source; every reachable per-eye runtime branch drawn on Metal. Inactive single-pass variants intentionally stripped; build filter asserts reachable sets.",
    }
for project, helperpath in [
    ("unity", "Assets/Editor/NativeOriginalVariantFilter.cs"),
    ("unity-full", "Assets/Editor/NativeBuildTools/NativeOriginalVariantFilter.cs"),
]:
    for dep in (snapshot / "Assets").rglob("*.cginc"):
        dest = root / project / dep.relative_to(snapshot)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(dep, dest)
    shutil.copyfile(root / "shaders/NativeOriginalVariantFilter.cs", root / project / helperpath)
(root / "shaders/validated-shader-overrides.json").write_text(json.dumps(overrides, indent=2))
(root / "shaders/staged-runtime-variants.json").write_text(json.dumps(staged, indent=2))
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
                "variant_status": "All original runtime branches recovered; reachable per-eye runtime branch GPU proof, inactive single-pass source preserved.",
            }
        )
(root / "shaders/working-shader-manifest.json").write_text(json.dumps(main, indent=2))
print(
    "Staged",
    len(staged),
    "runtime shader targets and shared includes; GUIDs preserved. Invoke Configure() before building.",
)
