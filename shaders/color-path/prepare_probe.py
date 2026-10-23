from pathlib import Path
import re, json, hashlib

root = Path(__file__).resolve().parents[2]
out = root / "color-path-probe/Assets/Resources"
s = (root / "unity-full/Assets/Shader/Hidden_PostProcessing_Uber.shader").read_text()
rows = []
for mt in re.finditer(r'^\s*#(?:if|elif) (.*)\n\s*#include "([^"]+)"', s, re.M):
    cond, path = mt.groups()
    keys = set(re.findall(r"(?<!!)defined\((\w+)\)", cond))
    if keys not in (
        {"COLOR_GRADING_HDR_3D", "FINALPASS"},
        {"COLOR_GRADING_HDR_3D", "FINALPASS", "BLOOM"},
    ):
        continue
    body = (root / "unity-full" / path).read_text()
    fixed, n = re.subn(
        r"(?P<dst>tmp\d+\.yz) = (?P<src>tmp\d+\.xy) \* _Lut3D_Params;",
        r"\g<dst> = \g<src> * _Lut3D_Params.yy;",
        body,
    )
    assert n == 1, (path, n)
    fixed, n = re.subn(
        r"(?P<dst>tmp\d+\.yz) = (?P<src>tmp\d+\.yz) \* _Lut3D_Params \+",
        r"\g<dst> = \g<src> * _Lut3D_Params.xx +",
        fixed,
    )
    assert n == 1
    index = len(rows)
    for label, text in [
        ("Broken", body),
        ("SwizzleOnly", fixed),
        ("Corrected", re.sub(r"\bexp\(", "exp2(", re.sub(r"\blog\(", "log2(", fixed))),
    ]:
        (out / f"{label}{index}.shader").write_text(
            'Shader "ColorPath/'
            + label
            + str(index)
            + '" { SubShader { Cull Off ZWrite Off ZTest Always Pass { CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n#pragma target 4.5\n'
            + text
            + "\nENDCG } } }\n"
        )
    rows.append(
        {
            "index": index,
            "source": path,
            "keywords": sorted(keys),
            "before_sha256": hashlib.sha256(body.encode()).hexdigest(),
            "after_sha256": hashlib.sha256(fixed.encode()).hexdigest(),
        }
    )
(root / "shaders/color-path/probe-inputs.json").write_text(json.dumps(rows, indent=2))
assert len(rows) == 4
