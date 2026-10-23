"""Restore validated source into full graph without replacing graph GUIDs.
Duplicate-name shaders require authoritative full-export identity, not filename suffix guesses.
"""

from pathlib import Path
import collections, json, re, shutil, hashlib, subprocess

root = Path(__file__).resolve().parents[1]
work = root / "unity-full/Assets"
probe = root / "shader-probe/Assets"
original = root / "shaders/final-recovery/ExportedProject/Assets/Shader"
out = root / "shaders/staged-full-sources.json"


def info(p):
    s = p.read_text()
    name = re.search(r'Shader\s+"([^"]+)"', s)[1]
    prop = re.sub(r"\s+", "", s.split("SubShader", 1)[0].split("//Dummy", 1)[0]).replace(
        ",Color)", ",Vector)"
    )
    return name, prop


byname = collections.defaultdict(list)
for p in original.glob("*.shader"):
    byname[info(p)[0]].append(p)
report = (
    json.loads(out.read_text()) if out.exists() else {"shaders": [], "computes": [], "pending": []}
)
already = {r["target"] for r in report["shaders"]}
report["pending"] = []
for p in sorted(work.rglob("*.shader")):
    rel = str(p.relative_to(root))
    if rel in already:
        continue
    name, prop = info(p)
    candidates = byname[name]
    matches = [q for q in candidates if info(q)[1] == prop]
    choices = candidates if len(candidates) == 1 else matches
    if len(choices) != 1:
        report["pending"].append(
            {
                "target": rel,
                "name": name,
                "candidate_sources": [str(q.relative_to(root)) for q in choices],
            }
        )
        continue
    source = probe / "Resources/Shaders" / choices[0].name
    assert info(source)[0] == name
    meta = p.with_suffix(".shader.meta").read_bytes()
    guid = re.search(rb"guid: (\w+)", meta)[1].decode()
    originalhash = hashlib.sha256(p.read_bytes()).hexdigest()
    p.write_bytes(source.read_bytes())
    assert p.with_suffix(".shader.meta").read_bytes() == meta
    report["shaders"].append(
        {
            "name": name,
            "target": rel,
            "source": str(source.relative_to(root)),
            "guid": guid,
            "original_sha256": originalhash,
            "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "identity": "unique original name"
            if len(candidates) == 1
            else "unique original name and normalized Properties",
        }
    )
shutil.copytree(probe / "NativePostProcessing", work / "NativePostProcessing", dirs_exist_ok=True)
shutil.copy2(
    root / "shaders/postprocessing-2017/LICENSE", work / "NativePostProcessing/LICENSE.txt"
)
guids = []
for source in sorted((probe / "Resources/Computes").glob("*.compute")):
    old = work / "ComputeShader" / (source.stem + ".asset")
    target = old.with_suffix(".compute")
    meta = old.with_suffix(".asset.meta") if old.exists() else target.with_suffix(".compute.meta")
    guid = re.search(r"guid: (\w+)", meta.read_text())[1]
    guids.append(guid)
    if old.exists():
        target.write_bytes(source.read_bytes())
        target.with_suffix(".compute.meta").write_text(
            f"fileFormatVersion: 2\nguid: {guid}\nComputeShaderImporter:\n  externalObjects: {{}}\n  currentAPIMask: 65536\n  preprocessorOverride: 0\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
        )
        old.unlink()
        meta.unlink()
        report["computes"].append(
            {
                "name": source.stem,
                "target": str(target.relative_to(root)),
                "guid": guid,
                "source": str(source.relative_to(root)),
                "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            }
        )
found = subprocess.run(
    [
        "rg",
        "-l",
        "guid: (" + "|".join(guids) + ")",
        str(work),
        "-g",
        "*.asset",
        "-g",
        "*.prefab",
        "-g",
        "*.unity",
    ],
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
report.setdefault("compute_reference_files", []).extend(refs)
out.write_text(json.dumps(report, indent=2))
print({k: len(v) for k, v in report.items()})
