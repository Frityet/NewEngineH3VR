"""Restore only unsupported gather4 instructions from exact original FinalPass DXBC.
Modern shared exporter owns all other sampling and predicate semantics. No inferred
texture arithmetic or blanket marker removal is permitted.
"""

from pathlib import Path
import json, re

root = Path(__file__).resolve().parents[1]


def restore(source):
    assert re.search(r'Shader "([^"]+)"', source)[1] == "Hidden/PostProcessing/FinalPass"
    proof = root / "shaders/critical-bytecode-proof"
    programs = json.loads((proof / "manifest.json").read_text())
    records = []
    pattern = re.compile(
        r"(?P<condition>^[ \t]*#(?:if|elif) [^\n]+\n)(?P<body>.*?)(?=^[ \t]*#(?:elif|else)\b)",
        re.M | re.S,
    )

    def branch(m):
        body = m["body"]
        if "//unsupported_gather4;" not in body:
            return m[0]
        keys = set(re.findall(r"// Keywords: ([^\n]*)", body)[-1].split())
        p = next(p for p in programs if set(p["keywords"]) == keys and p["type"].endswith("SM50"))
        asm = (proof / p["file"]).with_suffix(".asm").read_text()
        gathers = [s.strip() for s in asm.splitlines() if s.strip().startswith("gather4_")]
        assert len(gathers) == body.count("//unsupported_gather4;")
        for inst in gathers:
            op, args = inst.split(" ", 1)
            dest, uv, resource, sampler = args.split(", ")
            dr, dm = dest.split(".")
            tr, ts = resource.split(".")
            assert tr == "t1"
            assert "Texture2D<float4> _MainTex;" in body and "SamplerState sampler_MainTex;" in body
            ur, um = uv.split(".")
            if ur.startswith("r"):
                coord = ur.replace("r", "tmp", 1) + "." + um[:2]
            else:
                assert ur == "v1" and um[:2] == "xy"
                coord = "inp.texcoord.xy"
            swizzle = "".join(ts["xyzw".index(c)] for c in dm)
            component = {"x": "Red", "y": "Green", "z": "Blue", "w": "Alpha"}[sampler.split(".")[1]]
            off = re.search(r"aoffimmi_indexable\((-?\d+),(-?\d+),(-?\d+)\)", op)
            offset = "" if not off else ", int2(" + off[1] + ", " + off[2] + ")"
            assert not off or off[3] == "0"
            replacement = (
                dr.replace("r", "tmp", 1)
                + "."
                + dm
                + " = _MainTex.Gather"
                + component
                + "(sampler_MainTex, "
                + coord
                + offset
                + ")."
                + swizzle
                + ";"
            )
            body = body.replace("//unsupported_gather4;", replacement, 1)
            records.append(
                {
                    "original_program": p,
                    "original_instruction": inst,
                    "restored_statement": replacement,
                }
            )
        return m["condition"] + body

    result = pattern.sub(branch, source)
    assert "unsupported_gather4" not in result
    assert len(records) == 64, len(records)
    return result, records
