"""Prepare experiments only; do not install untested shader variants into game graphs."""

from pathlib import Path
import re, json, hashlib
from repair_stereo_matrix_indices import repair

root = Path(__file__).resolve().parents[1]
src = root / "shaders/variants-grouped"
dest = root / "shaders/optics-pending"
dest.mkdir(exist_ok=True)
files = [
    "Reflex Sight_Reflex Sight.shader",
    "Reflex Sight_Reflex Sight_0.shader",
    "Reflex Sight_Reflex Sight Occluded (Fiber Optic).shader",
    "Unlit_ScopeClearLens.shader",
    "Unlit_ScopeClearLens_0.shader",
    "Hidden_PIPScope_PostProcess.shader",
]
rows = []
for name in files:
    original = (src / name).read_text()
    text, count = repair(original)

    def pragmas(m):
        body = re.sub(r"^[ \t]*#pragma (vertex vert|fragment frag)[ \t]*\n", "", m[1], flags=re.M)
        return "CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag" + body + "ENDCG"

    text = re.sub(r"CGPROGRAM(.*?)ENDCG", pragmas, text, flags=re.S)
    changes = []
    if name == "Hidden_PIPScope_PostProcess.shader":
        text, n = re.subn(r'"LIGHTMODE" = "META"', '"LIGHTMODE" = "Always"', text)
        changes.append({"retain_runtime_passes": n})
        # Original NVGBLUR writes RGB only and retains ColorMask RGB. Match the
        # validated baseline's vec4 output requirement without changing written RGB.
        n = 0

        def widen(m):
            global n
            body = m[1]
            if not re.search(r"float3 \w+ : SV_TARGET\d*;", body):
                return m[0]
            body, changed = re.subn(r"float3 (\w+) : (SV_TARGET\d*);", r"float4 \1 : \2;", body)
            n += changed
            body = body.replace("fout o;", "fout o; o.sv_target.w = 0.0;")
            return "CGPROGRAM" + body + "ENDCG"

        text = re.sub(r"CGPROGRAM(.*?)ENDCG", widen, text, flags=re.S)
        changes.append({"float4_fragment_outputs": n})
    (dest / name).write_text(text)
    rows.append(
        {
            "file": name,
            "input_sha256": hashlib.sha256(original.encode()).hexdigest(),
            "sha256": hashlib.sha256(text.encode()).hexdigest(),
            "stereo_indices": count,
            "other_changes": changes,
            "status": "not imported or GPU tested; do not stage",
        }
    )
(dest / "manifest.json").write_text(json.dumps(rows, indent=2))
print(json.dumps(rows, indent=2))
