"""Stage validated source restorations while retaining existing object GUIDs."""

from pathlib import Path
import json, re, shutil, subprocess

root = Path(__file__).resolve().parents[1]
probe = root / "shader-probe/Assets"
work = root / "unity/Assets"
backup = root / "shaders/pre-source-restoration"
backup.mkdir(exist_ok=True)
manifest = []
# Preserve original shader locations and GUIDs, replacing only source text.
selected = {r["name"]: r for r in json.load(open(root / "shaders/replacement-manifest.json"))}
source_rows = json.load(open(root / "shaders/postprocessing-2017-match.json"))
for r in source_rows:
    if "original_passes" not in r:
        continue
    target = Path(selected[r["name"]]["target"])
    shutil.copy2(target, backup / target.name)
    source = root / r["probe"]
    target.write_bytes(source.read_bytes())
    manifest.append(
        {
            "name": r["name"],
            "target": str(target.relative_to(root)),
            "kind": "historical-source",
            "source_revision": "23dfcb6f407f74704d8e23a52b21740ecb4baa3d",
        }
    )
target = Path(selected["Hidden/VRTrail"]["target"])
shutil.copy2(target, backup / target.name)
target.write_bytes((probe / "Resources/Shaders/Hidden_VRTrail.shader").read_bytes())
manifest.append(
    {
        "name": "Hidden/VRTrail",
        "target": str(target.relative_to(root)),
        "kind": "buffer-repair-from-original-CSharp-stride12-and-binding",
    }
)
shutil.copytree(probe / "NativePostProcessing", work / "NativePostProcessing", dirs_exist_ok=True)
shutil.copy2(
    root / "shaders/postprocessing-2017/LICENSE", work / "NativePostProcessing/LICENSE.txt"
)
# Keep the GUID and native object ID; change importer from native Windows bytecode to compute source.
guids = []
for source in sorted((probe / "Resources/Computes").glob("*.compute")):
    old = work / "ComputeShader" / (source.stem + ".asset")
    meta = old.with_suffix(".asset.meta")
    guid = re.search(r"guid: (\w+)", meta.read_text()).group(1)
    guids.append(guid)
    shutil.copy2(old, backup / old.name)
    shutil.copy2(meta, backup / meta.name)
    target = old.with_suffix(".compute")
    target.write_bytes(source.read_bytes())
    target.with_suffix(".compute.meta").write_text(
        f"fileFormatVersion: 2\nguid: {guid}\nComputeShaderImporter:\n  externalObjects: {{}}\n  preprocessorOverride: 0\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    )
    old.unlink()
    meta.unlink()
    manifest.append(
        {
            "name": source.stem,
            "guid": guid,
            "target": str(target.relative_to(root)),
            "kind": "historical-compute-source",
            "source_revision": "23dfcb6f407f74704d8e23a52b21740ecb4baa3d",
        }
    )
pattern = "guid: (" + "|".join(guids) + ")"
found = subprocess.run(
    ["rg", "-l", pattern, str(work), "-g", "*.asset", "-g", "*.prefab", "-g", "*.unity"],
    capture_output=True,
    text=True,
    check=False,
)
refs = []
for name in found.stdout.splitlines():
    p = Path(name)
    s = p.read_text()
    new = re.sub(r"(guid: (?:" + "|".join(guids) + r"), type: )2", r"\g<1>3", s)
    if new != s:
        p.write_text(new)
        refs.append(str(p.relative_to(root)))
(root / "shaders/staged-native-sources.json").write_text(
    json.dumps({"assets": manifest, "compute_reference_files": refs}, indent=2)
)
print("Staged", len(manifest), "assets; updated compute reference files", len(refs))
