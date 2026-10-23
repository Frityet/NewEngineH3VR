"""Apply all shader sources using independently verified v2 exporter graph identities."""

from pathlib import Path
import json, re, hashlib, shutil

root = Path(__file__).resolve().parents[1]
identity = json.load(open(root / "build/shader-identities/full-shader-identity-map.json"))
out = root / "shaders/staged-full-sources.json"
report = json.loads(out.read_text())
rows = []
overrides = (
    json.loads((root / "shaders/validated-shader-overrides.json").read_text())
    if (root / "shaders/validated-shader-overrides.json").exists()
    else {}
)
for row in identity:
    target = root / "unity-full" / row["exported_path"]
    source = root / "shaders/validated-runtime-sources" / Path(row["converted_shader_path"]).name
    if source.name in overrides:
        source = root / overrides[source.name]["source"]
    meta = target.with_suffix(".shader.meta").read_bytes()
    assert re.search(rb"guid: (\w+)", meta)[1].decode() == row["guid"]
    text = source.read_text()
    actual_name = re.search(r'Shader\s+"([^"]+)"', text)[1]
    assert (
        actual_name
        == re.search(
            r'Shader\s+"([^"]+)"',
            (root / "recovered-full/ExportedProject" / row["exported_path"]).read_text(),
        )[1]
    )
    assert "DummyShaderTextExporter" not in text

    if target.read_bytes() != source.read_bytes():
        target.write_bytes(source.read_bytes())
    assert target.with_suffix(".shader.meta").read_bytes() == meta
    rows.append(
        {
            "name": actual_name,
            "target": str(target.relative_to(root)),
            "source": str(source.relative_to(root)),
            "guid": row["guid"],
            "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "identity": "verified exporter collection/path-ID order and material pointers",
            "original_source_file": row["original_source_file"],
            "path_id": row["path_id"],
        }
    )
report["shaders"] = rows
report["pending"] = []
out.write_text(json.dumps(report, indent=2))
print("Staged", len(rows), "with exact identities and preserved GUIDs")

for dependency in (root / "shaders/alloy-validated-source/Assets/Alloy").rglob("*"):
    if dependency.is_file():
        target = (
            root
            / "unity-full/Assets/Alloy"
            / dependency.relative_to(root / "shaders/alloy-validated-source/Assets/Alloy")
        )
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(dependency, target)

for dependency in (root / "shaders/pip-packaged/Assets/NativeShaders/PIPScopeVariants").glob(
    "*.cginc"
):
    target = root / "unity-full/Assets/NativeShaders/PIPScopeVariants" / dependency.name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)

for dependency in (root / "shaders/alloy-historical-candidates/Assets/AlloyUnity56").rglob("*"):
    if dependency.is_file():
        target = (
            root
            / "unity-full/Assets/AlloyUnity56"
            / dependency.relative_to(
                root / "shaders/alloy-historical-candidates/Assets/AlloyUnity56"
            )
        )
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(dependency, target)

for dependency in (root / "shaders/camera-relative-validated/Assets").rglob("*.cginc"):
    target = (
        root / "unity-full" / dependency.relative_to(root / "shaders/camera-relative-validated")
    )
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)

for dependency in (root / "shaders/cliff-validated/Assets").rglob("*.cginc"):
    target = root / "unity-full" / dependency.relative_to(root / "shaders/cliff-validated")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)

for dependency in (root / "shaders/residual-validated/Assets").rglob("*.cginc"):
    target = root / "unity-full" / dependency.relative_to(root / "shaders/residual-validated")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)

for dependency in (root / "shaders/remaining-validated/Assets").rglob("*.cginc"):
    target = root / "unity-full" / dependency.relative_to(root / "shaders/remaining-validated")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)

for dependency in (root / "shaders/runtime-validated/Assets").rglob("*.cginc"):
    target = root / "unity-full" / dependency.relative_to(root / "shaders/runtime-validated")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)

# Exact tested semantic fixes supersede earlier recovered program dependencies.
for dependency in (root / "shaders/semantic-v2-validated/Assets").rglob("*.cginc"):
    target = root / "unity-full" / dependency.relative_to(root / "shaders/semantic-v2-validated")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependency, target)
