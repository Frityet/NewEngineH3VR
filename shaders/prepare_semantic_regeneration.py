"""Freeze exact current exported Shader identities for an isolated corrected re-export.
Original Steam and recovered graphs are read-only. This writes shader-only inputs and
current-source hashes; it does not stage source or change live Unity projects.
"""

from pathlib import Path
import json, re, collections, hashlib
import UnityPy

root = Path(__file__).resolve().parents[1]
out = root / "shaders/semantic-v2-inputs"
out.mkdir(exist_ok=True)
assert not any(out.iterdir()), "Refusing to overwrite frozen regeneration inputs"
full = json.loads((root / "build/shader-identities/full-shader-identity-map.json").read_text())
source = json.loads((root / "audit-assets/all-shader-source-map.json").read_text())
byorigin = {(r["source_file"], r["source_path_id"]): r for r in source}
selection = []
bycopy = collections.defaultdict(set)
variants = set()
preserve = []
additional = set(
    json.loads((root / "shaders/uncovered-recovery-selection.json").read_text())["names"]
)
for r in full:
    original = byorigin[(r["original_source_file"], r["path_id"])]
    p = root / "unity-full" / r["exported_path"]
    body = p.read_text()
    name = re.search(r'Shader "([^"]+)"', body)[1]
    recovered = (
        "Original shader has no recovered variant" in body
        or "// Keywords:" in body
        or bool(
            re.search(
                r'#include "Assets/NativeShaders/(?:PIPScope|CameraRelative|Cliff|Residual|Remaining|Runtime|Uncovered)Variants/',
                body,
            )
        )
    )
    if name == "Hidden/VRTrail":
        recovered = False  # Explicitly restored structured-buffer/SV_VertexID program; no texture sampling to regenerate.
    original_variants = "Original shader has no recovered variant" in body or name in additional
    if original_variants:
        variants.add(name)
    if not recovered:
        preserve.append(
            {
                "name": name,
                "path": str(p.relative_to(root)),
                "reason": "Current source is not emitted recovered HLSL; preserve historical/public/manual restoration pending exact review.",
            }
        )
    selection.append(
        dict(
            original,
            exported_path=r["exported_path"],
            guid=r["guid"],
            baseline_source=Path(r["converted_shader_path"]).name,
            current_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),
            current_recovered_hlsl=recovered,
            full_variant_recovery=original_variants,
        )
    )
    bycopy[original["copy_file"]].add(original["copy_path_id"])
for copy, ids in bycopy.items():
    env = UnityPy.load(str(root / "audit-assets/all-shader-inputs" / copy))
    written = False
    for asset in env.assets:
        asset.objects = {k: o for k, o in asset.objects.items() if o.path_id in ids}
        if asset.objects:
            assert not written
            (out / copy).write_bytes(asset.save())
            written = True
    assert written, copy
report = {
    "objects": selection,
    "full_variant_names": sorted(variants),
    "preserve_current_source": preserve,
    "scope": "Only exact277 current original shader identities; no graph modifications.",
}
(root / "shaders/semantic-v2-selection.json").write_text(json.dumps(report, indent=2))
print(
    "Selected",
    len(selection),
    "objects",
    len(bycopy),
    "files",
    len(variants),
    "full-variant names",
    len(preserve),
    "preserved source overrides",
)
