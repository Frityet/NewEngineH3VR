"""Stage native-proven shader upgrades only at a coordinated game import checkpoint.
Explicit project argument required; no .meta files are rewritten.
"""

from pathlib import Path
import json, re, hashlib, shutil, argparse

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("project", choices=["unity", "unity-full"])
parser.add_argument("--dry-run", action="store_true")
args = parser.parse_args()
read = lambda p: json.loads((root / p).read_text())
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
named = {}
for row in read("shaders/alloy-other-candidates.json"):
    named[row["name"]] = {
        "source": row["probe_source"],
        "validation": "shaders/alloy-families-native-gpu-report.json",
        "kind": "original Alloy public source; all serialized material keyword sets rendered",
    }
for row in read("shaders/alloy-compat-candidates.json"):
    if row["compatibility_shim"] is not None:
        continue
    named[row["name"]] = {
        "source": row["source"],
        "validation": "shaders/alloy-compat-native-gpu-report.json",
        "kind": "original Alloy terrain/SpeedTree source; native Metal representative feature sets rendered",
    }
for row in read("shaders/alloy-historical-candidates.json")["shaders"]:
    named[row["name"]] = {
        "source": row["source"],
        "validation": "shaders/alloy-historical-native-gpu-report.json",
        "kind": "original Alloy Unity 5.6 source with exact property ABI; material keyword sets rendered",
    }
for row in named.values():
    assert read(row["validation"])["passed"]
# Pin exactly tested inputs, independent of later probe experiments.
for name, row in named.items():
    source = root / row["source"]
    dest = root / "shaders/additional-validated-source" / source.name
    if not args.dry_run:
        dest.parent.mkdir(exist_ok=True)
        shutil.copyfile(source, dest)
    row["original_source"] = row["source"]
    row["source"] = str(dest.relative_to(root))
    row["bytes"] = source.read_bytes()
optics = {}
for source in (root / "shaders/optics-pending").glob("*.shader"):
    validation = source.with_name(source.stem + "-draw-report.json")
    r = json.loads(validation.read_text())
    assert r["passed"] and r["complete"] and len(r["variants"]) == r["expected"]
    optics[source.name] = {
        "source": str(source.relative_to(root)),
        "validation": str(validation.relative_to(root)),
        "kind": str(r["expected"])
        + " original pass-specific keyword branches drawn on native Metal",
        "bytes": source.read_bytes(),
    }
identity = read("build/shader-identities/full-shader-identity-map.json")
full = [
    {
        "name": r["name"],
        "target": "unity-full/" + r["exported_path"],
        "guid": r["guid"],
        "basename": Path(r["converted_shader_path"]).name,
    }
    for r in identity
]
main = [
    {
        "name": r["name"],
        "target": str(Path(r["target"]).relative_to(root)),
        "guid": r["meta_guid"],
        "basename": Path(r["selected"]["export_path"]).name,
    }
    for r in read("shaders/replacement-manifest.json")
    if r.get("selected")
]
rows = []
overrides = read("shaders/validated-shader-overrides.json")
for r in full if args.project == "unity-full" else main:
    selected = optics.get(r["basename"]) or named.get(r["name"])
    if not selected:
        continue
    target = root / r["target"]
    meta = target.with_suffix(".shader.meta")
    original_meta = meta.read_bytes()
    assert re.search(rb"guid: (\w+)", original_meta)[1].decode() == r["guid"]
    assert re.search(rb'Shader\s+"([^"]+)"', selected["bytes"])[1].decode() == r["name"]
    if not args.dry_run:
        target.write_bytes(selected["bytes"])
        assert meta.read_bytes() == original_meta
        overrides[r["basename"]] = {
            "source": selected["source"],
            "validation": selected["validation"],
            "variant_status": selected["kind"],
        }
    rows.append(
        {
            **r,
            **{k: v for k, v in selected.items() if k != "bytes"},
            "sha256": hashlib.sha256(selected["bytes"]).hexdigest(),
        }
    )
if not args.dry_run:
    # Every include remains under its original or isolated historical namespace.
    for source_dir, dest_dir in [
        ("shaders/alloy-validated-source/Assets/Alloy", "Assets/Alloy"),
        ("shaders/alloy-historical-candidates/Assets/AlloyUnity56", "Assets/AlloyUnity56"),
    ]:
        for dependency in (root / source_dir).rglob("*"):
            if not dependency.is_file():
                continue
            target = root / args.project / dest_dir / dependency.relative_to(root / source_dir)
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.exists() or target.read_bytes() != dependency.read_bytes():
                shutil.copyfile(dependency, target)
    (root / "shaders/validated-shader-overrides.json").write_text(json.dumps(overrides, indent=2))
    (root / f"shaders/staged-additional-{args.project}.json").write_text(json.dumps(rows, indent=2))
    if args.project == "unity-full":
        manifest = read("shaders/staged-full-sources.json")
        updates = {r["target"]: r for r in rows}
        for r in manifest["shaders"]:
            if r["target"] in updates:
                r.update({k: updates[r["target"]][k] for k in ["source", "sha256", "validation"]})
        (root / "shaders/staged-full-sources.json").write_text(json.dumps(manifest, indent=2))
    else:
        manifest = read("shaders/working-shader-manifest.json")
        updates = {r["target"]: r for r in rows}
        for r in manifest:
            r["sha256"] = sha(root / r["path"])
            if r["path"] in updates:
                r["variant_status"] = updates[r["path"]]["kind"]
                r["validation"] = updates[r["path"]]["validation"]
        (root / "shaders/working-shader-manifest.json").write_text(json.dumps(manifest, indent=2))
print(
    "DRY RUN" if args.dry_run else "STAGED",
    len(rows),
    "files",
    len({r["name"] for r in rows}),
    "names",
    args.project,
)
