"""Repair/package exact original programs; create separate subshader probes only."""

from pathlib import Path
import re, json, hashlib, itertools, shutil, sys

batch = (
    "uncovered"
    if "--uncovered" in sys.argv
    else (
        "runtime"
        if "--runtime" in sys.argv
        else ("remaining" if "--remaining" in sys.argv else "residual")
    )
)
namespace = batch.capitalize() + "Variants"
from repair_stereo_matrix_indices import repair

root = Path(__file__).resolve().parents[1]
scope = {"re": re, "itertools": itertools}
code = (root / "shaders/normalize_variant_groups.py").read_text()
exec(code[code.index("def process(") : code.index("for p in src.glob")], scope)
out = root / f"shaders/{batch}-packaged"
inc = out / f"Assets/NativeShaders/{namespace}"
inc.mkdir(parents=True, exist_ok=True)
probe = root / f"{batch}-probe"
(probe / "Assets/Resources/Shaders").mkdir(parents=True, exist_ok=True)
(probe / "Assets/Editor").mkdir(exist_ok=True)
for d in ["ProjectSettings", "Packages"]:
    (probe / d).mkdir(exist_ok=True)
    for f in (root / "cliff-probe" / d).glob("*"):
        if f.is_file():
            shutil.copyfile(f, probe / d / f.name)
pattern = re.compile(
    r"(?P<condition>^[ \t]*#(?:if|elif) [^\n]+\n)(?P<body>.*?)(?=^[ \t]*#(?:elif|else)\b)",
    re.M | re.S,
)
unique = {}
rows = []
probes = []
identical = {}


def blocks(s, token):
    for m in re.finditer(r"\b" + token + r"\s*\{", s):
        depth = 1
        i = m.end()
        while depth:
            depth += (s[i] == "{") - (s[i] == "}")
            i += 1
        yield m.start(), i, s[m.start() : i]


for p in sorted(
    (root / f"shaders/{batch}-variant-recovery/ExportedProject/Assets/Shader").glob("*.shader")
):
    original = p.read_text()
    sha = hashlib.sha256(original.encode()).hexdigest()
    if sha in identical:
        rows.append({"file": p.name, "equivalent_to": identical[sha], "original_sha256": sha})
        continue
    identical[sha] = p.name
    stats = []
    s = re.sub(
        r"CGPROGRAM(.*?)ENDCG",
        lambda m: "CGPROGRAM" + scope["process"](m[1], stats) + "ENDCG",
        original,
        flags=re.S,
    )
    s, n = repair(s, preserve_unchanged_scopes=batch != "residual")

    def lift(m):
        return (
            "CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n"
            + re.sub(r"^[ \t]*#pragma (vertex vert|fragment frag)[ \t]*\n", "", m[1], flags=re.M)
            + "ENDCG"
        )

    s = re.sub(r"CGPROGRAM(.*?)ENDCG", lift, s, flags=re.S)

    def branch(m):
        body = m["body"]
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
        if "CBUFFER_START(UnityInstancing_PerDraw0)" in body:
            # Unity 2021 supplies structured per-instance fields in its own built-in CB;
            # the original 5.6 flat matrix arrays have a different runtime binding layout.
            body = re.sub(r"CBUFFER_START\(UnityDrawCallInfo\).*?CBUFFER_END", "", body, flags=re.S)
            body = re.sub(
                r"CBUFFER_START\(UnityInstancing_PerDraw0\).*?CBUFFER_END", "", body, flags=re.S
            )
            body = body.replace("v.sv_instanceid.x", "v.instanceID")
            body = re.sub(
                r"unity_ObjectToWorldArray\[([^\]]+)\]",
                r"unity_Builtins0Array[\1].unity_ObjectToWorldArray",
                body,
            )
            body = re.sub(
                r"unity_WorldToObjectArray\[([^\]]+)\]",
                r"unity_Builtins0Array[\1].unity_WorldToObjectArray",
                body,
            )
            body = body.replace(
                "v2f vert(appdata_full v)\n\t\t\t{",
                "v2f vert(appdata_full v)\n\t\t\t{\n UNITY_SETUP_INSTANCE_ID(v);",
            )
        sha = hashlib.sha256(body.encode()).hexdigest()
        unique[sha] = len(body)
        (inc / (sha + ".cginc")).write_text(body)
        return m["condition"] + f'#include "Assets/NativeShaders/{namespace}/{sha}.cginc"\n'

    s = pattern.sub(branch, s).replace("Mode 0", "Mode Off")
    # Give originally unnamed passes stable identifiers for exact build callbacks.
    # Names do not change pass order, state or program bodies.
    delta = 0
    for si, (a, b, sub) in enumerate(list(blocks(s, "SubShader"))):
        shift = 0
        for pi, (pa, pb, body) in enumerate(list(blocks(sub, "Pass"))):
            header = body[: body.index("CGPROGRAM")]
            if re.search(r'\bName\s+"[^"]*"', header):
                continue
            replacement = body.replace(
                "Pass {", f'Pass {{\nName "NATIVE_RECOVERED_SUB{si}_PASS{pi}"', 1
            )
            sub = sub[: pa + shift] + replacement + sub[pb + shift :]
            shift += len(replacement) - (pb - pa)
        s = s[: a + delta] + sub + s[b + delta :]
        delta += len(sub) - (b - a)
    (out / p.name).write_text(s)
    subs = list(blocks(s, "SubShader"))
    prefix = s[: subs[0][0]]
    for si, (start, end, sub) in enumerate(subs):
        # Every original subshader is independently exercised. Names are changed only
        # in the probe clones so runtime pass lookup cannot confuse duplicate names.
        passes = []
        offset = 0
        for pi, (a, b, body) in enumerate(list(blocks(sub, "Pass"))):
            header = body[: body.index("CGPROGRAM")]
            name = "NATIVE_PASS_" + str(pi)
            if re.search(r'\bName\s+"[^"]*"', header):
                replacement = re.sub(r'\bName\s+"[^"]*"', f'Name "{name}"', body, count=1)
            else:
                replacement = body.replace("Pass {", f'Pass {{\nName "{name}"', 1)
            mode = re.search(r'"LIGHTMODE"\s*=\s*"([^"]+)"', header)
            mode = mode[1].upper() if mode else "ALWAYS"
            if mode == "ALWAYS":
                replacement = (
                    replacement.replace("Tags {", 'Tags { "LIGHTMODE" = "ALWAYS"', 1)
                    if "Tags {" in header
                    else replacement.replace("Pass {", 'Pass {\nTags { "LIGHTMODE" = "ALWAYS" }', 1)
                )
            keys = [
                " ".join(sorted(re.findall(r"(?<!!)defined\((\w+)\)", k)))
                for k in re.findall(r"^\s*#(?:if|elif) (.+)$", body, re.M)
            ]
            passes.append({"passName": name, "lightMode": mode, "index": pi, "allowed": keys})
            sub = sub[: a + offset] + replacement + sub[b + offset :]
            offset += len(replacement) - (b - a)
        name = p.stem + "__Sub" + str(si) + ".shader"
        test = re.sub(
            r'Shader "[^"]+"',
            f'Shader "NativeProbe/{p.stem}/Sub{si}"',
            prefix + sub + "\n}",
            count=1,
        )
        (probe / "Assets/Resources/Shaders" / name).write_text(test)
        probes.append(
            {
                "file": name,
                "name": "NativeProbe/" + p.stem + "/Sub" + str(si),
                "source_file": p.name,
                "subshader": si,
                "passes": passes,
            }
        )
    rows.append(
        {
            "file": p.name,
            "original_sha256": sha,
            "packed_sha256": hashlib.sha256(s.encode()).hexdigest(),
            "stereo_index_repairs": n,
            "groups": stats,
            "subshaders": len(subs),
        }
    )
probeinc = probe / f"Assets/NativeShaders/{namespace}"
probeinc.mkdir(parents=True, exist_ok=True)
for p in inc.glob("*.cginc"):
    shutil.copyfile(p, probeinc / p.name)
(probe / "Assets/Resources/OriginalVariants.json").write_text(
    json.dumps({"shaders": probes}, indent=2)
)
(out / "manifest.json").write_text(
    json.dumps(
        {
            "shaders": rows,
            "probes": probes,
            "shared_include_count": len(unique),
            "shared_include_bytes": sum(unique.values()),
        },
        indent=2,
    )
)
print(
    "Unique original versions",
    len(identical),
    "probe subshaders",
    len(probes),
    "branches",
    sum(len(p["allowed"]) for s in probes for p in s["passes"]),
    "include bytes",
    sum(unique.values()),
)
