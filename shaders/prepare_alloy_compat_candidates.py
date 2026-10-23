"""Prepare isolated Alloy source candidates; never writes game projects."""

from pathlib import Path
import json, re

root = Path(__file__).resolve().parents[1]
match = json.loads((root / "shaders/alloy-source-match.json").read_text())["shaders"]
audit = json.loads((root / "shaders/material-keyword-audit.json").read_text())["shaders"]
names = [
    "Alloy/TriPlanar",
    "Alloy/Directional Blend",
    "Alloy/Vertex Blend",
    "Alloy/Nature/SpeedTree (Forward)",
    "Alloy/Nature/Terrain/4Splat",
    "Hidden/Alloy/Nature/Terrain/Details0",
    "Hidden/Alloy/Nature/Terrain/Details1",
    "Hidden/Alloy/Nature/Terrain/Details2",
    "Hidden/Alloy/Nature/Terrain/Distant",
]
result = []
lines = []
for name in names:
    row = next(r for r in match if r["name"] == name)
    source = root / row["candidates"][0]["source"]
    text = source.read_text()
    shim = None
    if name in names[:3]:
        mode = "DIRECTIONALBLENDMODE" if name == "Alloy/Directional Blend" else "TRIPLANARMODE"
        old = "_" + mode + "_WORLD"
        new = "_" + mode + "_OBJECT"
        prop = "_DirectionalBlendMode" if name == "Alloy/Directional Blend" else "_TriplanarMode"
        text = text.replace("[Toggle(" + new + ")]", "[Toggle(" + old + ")]")
        text = re.sub(
            r"(^\s*" + prop + r"\s*\([^\n]+\)\s*=\s*)0",
            lambda m: m[1].replace("World:{}, Object:{}", "Object:{}, World:{}") + "1",
            text,
            flags=re.M,
        )
        text = text.replace("#pragma shader_feature " + new, "#pragma shader_feature " + old)
        shim = (
            "// Original material ABI: AlloyMigrationTools.cs maps WORLD on to OBJECT off.\n#ifndef "
            + old
            + "\n#define "
            + new
            + " 1\n#endif\n"
        )
        text = text.replace("CGPROGRAM", "CGPROGRAM\n" + shim)
    filename = name.replace("/", "_") + ".shader"
    dest = root / "shaders/alloy-compat-candidates" / filename
    dest.parent.mkdir(exist_ok=True)
    dest.write_text(text)
    probe = root / "alloy-probe/Assets/Resources/Shaders" / filename
    probe.write_text(text)
    keys = {k for r in audit if r["name"] == name for k in r["sets"]}
    if name in names[:3]:
        keys |= {"", old}
    elif "SpeedTree" in name:
        keys |= {
            g + x
            for g in [
                "GEOM_TYPE_BRANCH",
                "GEOM_TYPE_BRANCH_DETAIL",
                "GEOM_TYPE_FROND",
                "GEOM_TYPE_LEAF",
                "GEOM_TYPE_MESH",
            ]
            for x in ["", " EFFECT_BUMP EFFECT_HUE_VARIATION"]
        }
    else:
        keys |= {
            "",
            "_METALLICGLOSSMAP",
            "_DETAIL_MULX2",
            "_METALLICGLOSSMAP _DETAIL_MULX2 _TERRAIN_NORMAL_MAP",
            "_SPECULARHIGHLIGHTS_OFF _GLOSSYREFLECTIONS_OFF",
        }
    for key in sorted(keys):
        lines.append(filename[:-7] + "|" + key)
    result.append(
        {
            "name": name,
            "upstream_source": str(source.relative_to(root)),
            "source": str(dest.relative_to(root)),
            "probe_source": str(probe.relative_to(root)),
            "keyword_sets": sorted(keys),
            "compatibility_shim": shim,
            "proof": "AlloyMigrationTools.cs lines 259-280 define the exact WORLD-to-OBJECT inversion; no material mutation."
            if shim
            else "Original property ABI differs only in Int serialization or numeric Range spelling.",
        }
    )
(root / "shaders/alloy-compat-candidates.json").write_text(json.dumps(result, indent=2))
(root / "alloy-probe/Assets/Resources/AlloyCompatKeywords.txt").write_text("\n".join(lines) + "\n")
print(len(result), "families", len(lines), "keyword sets")
