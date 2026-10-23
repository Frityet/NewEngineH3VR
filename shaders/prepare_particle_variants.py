from pathlib import Path
import re, json, hashlib, itertools
from repair_stereo_matrix_indices import repair

root = Path(__file__).resolve().parents[1]
scope = {"re": re, "itertools": itertools}
code = (root / "shaders/normalize_variant_groups.py").read_text()
exec(code[code.index("def process(") : code.index("for p in src.glob")], scope)
out = root / "shaders/particle-variants"
out.mkdir(exist_ok=True)
rows = []
for name in [
    "Particles_Vertex Lit Smooth Blended H3VR.shader",
    "Particles_Vertex Lit Smooth Blended H3VR_0.shader",
]:
    p = root / "shaders/particle-variant-recovery/ExportedProject/Assets/Shader" / name
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
