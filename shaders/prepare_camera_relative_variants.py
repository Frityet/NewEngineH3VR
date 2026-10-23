from pathlib import Path
import re, json, hashlib, itertools
from repair_stereo_matrix_indices import repair

root = Path(__file__).resolve().parents[1]
scope = {"re": re, "itertools": itertools}
code = (root / "shaders/normalize_variant_groups.py").read_text()
exec(code[code.index("def process(") : code.index("for p in src.glob")], scope)
out = root / "shaders/camera-relative-variants"
out.mkdir(exist_ok=True)
rows = []
for name in [
    "Alloy_Core (Camera Relative).shader",
    "Alloy_Core (Camera Relative)_0.shader",
    "Alloy_Core (Camera Relative)_1.shader",
]:
    p = root / "shaders/camera-relative-variant-recovery/ExportedProject/Assets/Shader" / name
    s = p.read_text()
    stats = []
    s = re.sub(
        r"CGPROGRAM(.*?)ENDCG",
        lambda m: "CGPROGRAM" + scope["process"](m[1], stats) + "ENDCG",
        s,
        flags=re.S,
    )
    s, n = repair(s)

    def lift(m):
        return (
            "CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n"
            + re.sub(r"^[ \t]*#pragma (vertex vert|fragment frag)[ \t]*\n", "", m[1], flags=re.M)
            + "ENDCG"
        )

    s = re.sub(r"CGPROGRAM(.*?)ENDCG", lift, s, flags=re.S)

    def shadow_declaration(m):
        body = m[2]
        uses = set(re.findall(r"(\w+)\(_ShadowMapTexture", body))
        decl = None
        if "UNITY_SAMPLE_SHADOW" in uses:
            decl = "UNITY_DECLARE_SHADOWMAP(_ShadowMapTexture);"
        elif "UNITY_SAMPLE_TEXCUBE_SAMPLER" in uses:
            decl = "UNITY_DECLARE_TEXCUBE(_ShadowMapTexture);"
        elif "UNITY_SAMPLE_TEX2D_SAMPLER" in uses:
            decl = "UNITY_DECLARE_TEX2D(_ShadowMapTexture);"
        if decl:
            body = body.replace('#include "UnityCG.cginc"', '#include "UnityCG.cginc"\n' + decl, 1)
        return m[1] + body

    s = re.sub(
        r"(^[ \t]*#(?:if|elif) [^\n]+\n)(.*?)(?=^[ \t]*#(?:elif|else)\b)",
        shadow_declaration,
        s,
        flags=re.M | re.S,
    )
    s = s.replace("Mode 0", "Mode Off")
    (out / name).write_text(s)
    (root / "shader-probe/Assets/Resources/Shaders" / name).write_text(s)
    rows.append(
        {
            "file": name,
            "original_sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
            "sha256": hashlib.sha256(s.encode()).hexdigest(),
            "stereo_index_repairs": n,
            "groups": stats,
        }
    )
(out / "manifest.json").write_text(json.dumps(rows, indent=2))
print([(r["file"], r["groups"]) for r in rows])
