"""Stage the pinned semantic correction at an explicitly authorized idle checkpoint."""

from pathlib import Path
import json, re, hashlib, shutil, sys
from verify_shader_proof_logs import require_clean

if "--apply" not in sys.argv:
    raise SystemExit("Use --apply only at the coordinated idle checkpoint")
root = Path(__file__).resolve().parents[1]
snapshot = root / "shaders/semantic-v2-validated"
load = lambda p: json.loads((root / p).read_text())
proof = load("shaders/semantic-v2-validated/manifest.json")
assert proof["passed"]


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


for batch, ref in proof["proofs"].items():
    assert sha(root / ref["path"]) == ref["sha256"]
    require_clean(
        root / ("shaders/" + batch + "-build.log"),
        root / ("shaders/" + batch + "-native-player.log"),
    )
for row in proof["rows"]:
    assert sha(root / row["source"]) == row["sha256"]
for rel, digest in proof["dependencies"].items():
    assert sha(snapshot / rel) == digest
selection = load("shaders/semantic-v2-selection.json")
preserved = []
for r in selection["objects"]:
    target = root / "unity-full" / r["exported_path"]
    assert sha(target) == r["current_sha256"], str(target)
    if not r["current_recovered_hlsl"]:
        preserved.append({"path": str(target.relative_to(root)), "sha256": sha(target)})
mainmap = load("shaders/replacement-manifest.json")
overrides = load("shaders/validated-shader-overrides.json")
staged = []
for row in proof["rows"]:
    identity = row["identity"]
    source = root / row["source"]
    targets = [("unity-full/" + identity["exported_path"], identity["guid"])]
    for main in mainmap:
        s = main.get("selected")
        if (
            s
            and s["source_file"] == identity["source_file"]
            and s["source_path_id"] == identity["source_path_id"]
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
        before = sha(target)
        target.write_bytes(source.read_bytes())
        assert meta.read_bytes() == original
        staged.append(
            {
                "target": path,
                "guid": guid,
                "meta_sha256": hashlib.sha256(original).hexdigest(),
                "before_sha256": before,
                "source": row["source"],
                "sha256": row["sha256"],
                "validation": "shaders/semantic-v2-validated/manifest.json",
            }
        )
    overrides[identity["baseline_source"]] = {
        "source": row["source"],
        "validation": "shaders/semantic-v2-validated/manifest.json",
        "variant_status": "All original paired branches preserved; reachable per-eye mono variants build retained and Metal draw/readback verified. Sampling/predicate semantics corrected with original instruction regressions; no complete Windows pixel equivalence claim.",
    }
for project, helper in [
    ("unity", "Assets/Editor/NativeOriginalVariantFilter.cs"),
    ("unity-full", "Assets/Editor/NativeBuildTools/NativeOriginalVariantFilter.cs"),
]:
    for rel, digest in proof["dependencies"].items():
        dest = root / project / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(snapshot / rel, dest)
        assert sha(dest) == digest
    shutil.copyfile(root / "shaders/NativeOriginalVariantFilter.cs", root / project / helper)
for r in preserved:
    assert sha(root / r["path"]) == r["sha256"]
(root / "shaders/validated-shader-overrides.json").write_text(json.dumps(overrides, indent=2))
bytarget = {r["target"]: r for r in staged}
full = load("shaders/staged-full-sources.json")
for row in full["shaders"]:
    if row["target"] in bytarget:
        row.update({k: bytarget[row["target"]][k] for k in ["source", "sha256", "validation"]})
(root / "shaders/staged-full-sources.json").write_text(json.dumps(full, indent=2))
main = load("shaders/working-shader-manifest.json")
for row in main:
    if row["path"] in bytarget:
        row.update(
            {
                "sha256": bytarget[row["path"]]["sha256"],
                "validation": "shaders/semantic-v2-validated/manifest.json",
                "variant_status": "Original paired variants with shared sampler/offset/swizzle and predicate repairs; native reachable mono branch execution verified.",
            }
        )
(root / "shaders/working-shader-manifest.json").write_text(json.dumps(main, indent=2))
for r in staged:
    assert sha(root / r["target"]) == r["sha256"]
    assert sha((root / r["target"]).with_suffix(".shader.meta")) == r["meta_sha256"]
for project, helper in [
    ("unity", "Assets/Editor/NativeOriginalVariantFilter.cs"),
    ("unity-full", "Assets/Editor/NativeBuildTools/NativeOriginalVariantFilter.cs"),
]:
    assert sha(root / project / helper) == sha(root / "shaders/NativeOriginalVariantFilter.cs")
report = {
    "passed": True,
    "staged": staged,
    "preserved_public_manual": preserved,
    "dependency_count": len(proof["dependencies"]),
    "snapshot_sha256": sha(snapshot / "manifest.json"),
    "helper_sha256": sha(root / "shaders/NativeOriginalVariantFilter.cs"),
    "refresh_started": False,
}
(root / "shaders/semantic-v2-staging-verification.json").write_text(json.dumps(report, indent=2))
print(
    "Staged",
    len(staged),
    "shader targets;",
    len(preserved),
    "public/manual full targets preserved; GUIDs unchanged",
)
