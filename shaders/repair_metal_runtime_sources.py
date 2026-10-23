from pathlib import Path
import json

root = Path(__file__).resolve().parents[1]
rows = []
for p in (root / "shader-probe/Assets/Resources/Shaders").glob("Unlit_Laser*.shader"):
    s = p.read_text()
    assert "float3 sv_target : SV_Target0;" in s
    s = s.replace("float3 sv_target : SV_Target0;", "float4 sv_target : SV_Target0;").replace(
        "fout o;", "fout o;\n                o.sv_target.w = 0.0;"
    )
    p.write_text(s)
    rows.append(
        {
            "file": p.name,
            "fix": "Metal BGRA render-target requires float4 output; RGB algorithm unchanged and additive alpha=0 preserves destination alpha",
        }
    )
p = root / "shader-probe/Assets/Resources/Shaders/Hidden_PIPScope_PostProcess.shader"
s = p.read_text()
count = s.count('"LIGHTMODE" = "META"')
assert count == 8
s = s.replace('"LIGHTMODE" = "META"', '"LIGHTMODE" = "Always"')
p.write_text(s)
rows.append(
    {
        "file": p.name,
        "fix": "Eight runtime Graphics.Blit passes had Meta tags, stripped from player; retain them as Always. Original PIPScope.cs explicitly selects pass indices0..7.",
    }
)
(root / "shaders/metal-runtime-source-repairs.json").write_text(json.dumps(rows, indent=2))
