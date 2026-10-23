"""Prepare a reviewable, isolated correction for recovered HDR 3D LUT variants.

This script never writes into either Unity project or accepted bundle tree. It copies
only affected SemanticV2Variants includes into this directory and records hashes.
"""

from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = ROOT / "unity-full/Assets/NativeShaders/SemanticV2Variants"
OUT_ROOT = Path(__file__).resolve().parent / "staging/Assets/NativeShaders/SemanticV2Variants"
REPORT = Path(__file__).resolve().parent / "staging-verification.json"
XY_SCALE = re.compile(r"(?m)^(\s*tmp\d+\.yz\s*=\s*tmp\d+\.xy\s*)\*\s*_Lut3D_Params(\s*;)")
SLICE_SCALE = re.compile(r"(?m)^(\s*tmp\d+\.yz\s*=\s*tmp\d+\.yz\s*)\*\s*_Lut3D_Params(\s*\+)")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


assert SOURCE_ROOT.is_dir(), SOURCE_ROOT
assert not OUT_ROOT.exists(), f"Refusing to overwrite {OUT_ROOT}"
OUT_ROOT.mkdir(parents=True)
rows = []
for src in sorted(SOURCE_ROOT.glob("*.cginc")):
    before = src.read_bytes()
    text = before.decode("utf-8")
    fixed, xy_count = XY_SCALE.subn(r"\1*_Lut3D_Params.yy\2", text)
    slice_fixed, slice_count = SLICE_SCALE.subn(r"\1*_Lut3D_Params.xx\2", fixed)
    if not xy_count and not slice_count:
        continue
    assert xy_count == 1 and slice_count == 1, (src, xy_count, slice_count)
    # DXBC uses cb0[32].yyyy for the input axes and cb0[32].xxxx for the
    # scale and half-texel offset in the subsequent LUT coordinate calculation.
    rel = src.relative_to(SOURCE_ROOT)
    dst = OUT_ROOT / rel
    dst.parent.mkdir(parents=True, exist_ok=True)
    after = slice_fixed.encode("utf-8")
    dst.write_bytes(after)
    rows.append(
        {
            "path": str(src.relative_to(ROOT)),
            "candidate": str(dst.relative_to(ROOT)),
            "source_sha256": sha(before),
            "candidate_sha256": sha(after),
            "replacements": {"input_axis": xy_count, "slice_scale": slice_count},
            "changed_lines": [
                line.strip()
                for line in text.splitlines()
                if XY_SCALE.fullmatch(line) or SLICE_SCALE.fullmatch(line)
            ],
            "candidate_lines": [
                line.strip()
                for line in slice_fixed.splitlines()
                if "_Lut3D_Params.yy" in line or "_Lut3D_Params.xx" in line
            ],
        }
    )
assert len(rows) == 384, len(rows)
REPORT.write_text(
    json.dumps(
        {
            "passed": True,
            "scope": "Isolated candidate copies only; no Unity project or accepted bundle changed.",
            "root_cause": "Recovered HLSL broadcasts float2 _Lut3D_Params across xy. Original DXBC uses cb0[32].yyyy for both R/G LUT axes, then cb0[32].xxxx for the blue slice coordinate.",
            "original_dxbc": "shaders/color-path/original/program-882.asm",
            "metal_pixel_proof": "color-path-probe/output/gpu-report.json",
            "variant_count": len(rows),
            "variants": rows,
        },
        indent=2,
    )
    + "\n"
)
print(f"Prepared {len(rows)} isolated candidate variants at {OUT_ROOT}")
