"""Restore serialized property-driven ShaderLab state in recovered HLSL sources.
Only named properties are changed; preserve other source repairs and pass bodies.
Legacy fixed-function Fog parameters are inventoried but intentionally not rewritten.
"""

from pathlib import Path
import re, json, hashlib

root = Path(__file__).resolve().parents[1]


def name(value):
    v = value.get("name")
    while isinstance(v, dict):
        v = v.get("name")
    return v if v and v != "<noninit>" else None


blend = [
    "Zero",
    "One",
    "DstColor",
    "SrcColor",
    "OneMinusDstColor",
    "SrcAlpha",
    "OneMinusSrcColor",
    "DstAlpha",
    "OneMinusDstAlpha",
    "SrcAlphaSaturate",
    "OneMinusSrcAlpha",
]
compare = [
    "Disabled",
    "Never",
    "Less",
    "Equal",
    "LEqual",
    "Greater",
    "NotEqual",
    "GEqual",
    "Always",
]
stencil = ["Keep", "Zero", "Replace", "IncrSat", "DecrSat", "Invert", "IncrWrap", "DecrWrap"]


def expr(value, enum=None):
    n = name(value)
    if n:
        return "[" + n + "]"
    return enum[int(value["val"])] if enum else format(value["val"], "g")


def patch_header(header, state):
    changes = []

    def line(command, value):
        nonlocal header
        text = "\t\t\t" + command + " " + value + "\n"
        pattern = r"(?m)^[ \t]*" + re.escape(command) + r"[ \t]+[^\n]+\n"
        found = re.search(pattern, header)
        if found:
            header = re.sub(pattern, lambda m: text, header, count=1)
        else:
            header += text
        changes.append(text.strip())

    b = state["rtBlend0"]
    fields = ["srcBlend", "destBlend", "srcBlendAlpha", "destBlendAlpha"]
    if any(name(b[k]) for k in fields):
        assert not state.get("rtSeparateBlend"), "Independent MRT blend needs explicit handling"
        line(
            "Blend",
            expr(b["srcBlend"], blend)
            + " "
            + expr(b["destBlend"], blend)
            + ", "
            + expr(b["srcBlendAlpha"], blend)
            + " "
            + expr(b["destBlendAlpha"], blend),
        )
    if name(b["colMask"]):
        line("ColorMask", expr(b["colMask"]))
    for field, command in [
        ("zWrite", "ZWrite"),
        ("zTest", "ZTest"),
        ("culling", "Cull"),
        ("alphaToMask", "AlphaToMask"),
    ]:
        if name(state[field]):
            line(command, expr(state[field]))
    if name(state["offsetFactor"]) or name(state["offsetUnits"]):
        line("Offset", expr(state["offsetFactor"]) + ", " + expr(state["offsetUnits"]))
    sfields = [state[k] for k in ["stencilRef", "stencilReadMask", "stencilWriteMask"]] + [
        v
        for op in ["stencilOp", "stencilOpFront", "stencilOpBack"]
        for v in state[op].values()
        if isinstance(v, dict)
    ]
    if any(name(v) for v in sfields):
        lines = [
            "Stencil {",
            "Ref " + expr(state["stencilRef"]),
            "ReadMask " + expr(state["stencilReadMask"]),
            "WriteMask " + expr(state["stencilWriteMask"]),
        ]
        for op, suffix in [
            ("stencilOp", ""),
            ("stencilOpFront", "Front"),
            ("stencilOpBack", "Back"),
        ]:
            d = state[op]
            if (
                suffix
                and not any(name(v) for v in d.values())
                and all(v.get("val", 0) == (8 if k == "comp" else 0) for k, v in d.items())
            ):
                continue
            lines.extend(
                command + suffix + " " + expr(d[key], enum)
                for key, command, enum in [
                    ("comp", "Comp", compare),
                    ("pass", "Pass", stencil),
                    ("fail", "Fail", stencil),
                    ("zFail", "ZFail", stencil),
                ]
            )
        lines.append("}")
        text = "\n".join("\t\t\t" + s for s in lines) + "\n"
        if re.search(r"\bStencil\s*\{", header):
            header = re.sub(r"(?m)^[ \t]*Stencil\s*\{[^}]*\}\s*\n", lambda m: text, header, count=1)
        else:
            header += text
        changes.append(text.strip())
    return header, changes


states = {
    (r["source_file"], r["path_id"]): r
    for r in json.load(open(root / "shaders/dynamic-render-state-audit.json"))
}
identity = json.load(open(root / "build/shader-identities/full-shader-identity-map.json"))
dest = root / "shaders/dynamic-state-candidates"
dest.mkdir(exist_ok=True)
rows = []
held = []
for row in identity:
    source = root / "shaders/validated-runtime-sources" / Path(row["converted_shader_path"]).name
    text = source.read_text()
    original = states.get((row["original_source_file"], row["path_id"]))
    changes = []
    if original:
        passes = list(re.finditer(r"(?m)^\s*Pass\s*\{.*?GpuProgramID[^\n]*\n", text, re.S))
        relevant = [
            p
            for p in original["passes"]
            if any(not r["field"].startswith("fog") for r in p["references"])
        ]
        if relevant:
            for p in reversed(relevant):
                if p["subshader"] != 0 or p["pass"] >= len(passes):
                    held.append(
                        {
                            "file": source.name,
                            "reason": "pass mapping requires review",
                            "original": p["name"],
                        }
                    )
                    continue
                match = passes[p["pass"]]
                header = match[0]
                match_name = re.search(r'\bName\s+"([^"]+)"', header)
                if match_name and match_name[1].upper() != str(p["name"]).upper():
                    held.append(
                        {
                            "file": source.name,
                            "reason": "pass name mismatch",
                            "source": match_name[1],
                            "original": p["name"],
                        }
                    )
                    continue
                repaired, edits = patch_header(header, p["state"])
                if edits:
                    text = text[: match.start()] + repaired + text[match.end() :]
                    changes.append({"pass": p["pass"], "name": p["name"], "directives": edits})
    (dest / source.name).write_text(text)
    if changes:
        rows.append(
            {
                "source": str(source.relative_to(root)),
                "candidate": str((dest / source.name).relative_to(root)),
                "target": row["exported_path"],
                "guid": row["guid"],
                "changes": changes,
                "sha256": hashlib.sha256(text.encode()).hexdigest(),
            }
        )
(root / "shaders/dynamic-state-repairs.json").write_text(
    json.dumps(
        {
            "changed": rows,
            "held": held,
            "not_modified": "legacy fixed-function fog parameters; programmable shader fog code remains original",
        },
        indent=2,
    )
)
print(
    "Changed files",
    len(rows),
    "directives",
    sum(len(p["directives"]) for r in rows for p in r["changes"]),
    "held",
    len(held),
)
print(held[:10])
