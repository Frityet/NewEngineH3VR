"""Restore FinalPass texture operations from original DXBC, without changing arithmetic.
Runs against isolated probe includes only. Each replacement is checked against original
instruction order, destination register/mask, and resource identity. Offsets and result
swizzles are explicit; gather component/order comes from the original instruction.
"""

from pathlib import Path
import re, json, hashlib

root = Path(__file__).resolve().parents[1]
proof = root / "shaders/critical-bytecode-proof"
probe = root / "critical-probe"
records = json.loads((proof / "manifest.json").read_text())
changes = []
seen = {}
for si in range(2):
    source = (
        probe / f"Assets/Resources/Shaders/Hidden_PostProcessing_FinalPass__Sub{si}.shader"
    ).read_text()
    for rel in re.findall(r'#include "(Assets/NativeShaders/[^\"]+)"', source):
        p = probe / rel
        if rel in seen:
            continue
        s = (root / "shaders/uncovered-packaged" / rel).read_text()
        keys = set(re.findall(r"// Keywords: ([^\n]*)", s)[-1].split())
        row = next(
            r
            for r in records
            if set(r["keywords"]) == keys and r["type"].endswith("SM50" if si == 0 else "SM40")
        )
        asm = (proof / row["file"]).with_suffix(".asm").read_text()
        instructions = [
            line.strip()
            for line in asm.splitlines()
            if re.match(r"\s*(sample(?:_l)?|gather4)(?:_|\s)", line)
        ]
        pattern = re.compile(
            r"(?P<indent>^[ \t]*)(?://unsupported_gather4;|(?P<dest>tmp\d+(?:\.[xyzw]+)?) = (?P<call>tex2D(?:lod)?\([^\n]+);)",
            re.M,
        )
        matches = list(pattern.finditer(s))
        assert len(matches) == len(instructions), (p, len(matches), len(instructions))
        notes = []

        def replace(m):
            inst = instructions[len(notes)]
            parts = inst.split(" ", 1)
            op = parts[0]
            args = parts[1].split(", ")
            dest, uv, resource, sampler = args[:4]
            dr, dm = dest.split(".")
            tr, ts = resource.split(".")
            texture = {"t0": "_DitheringTex", "t1": "_MainTex"}[tr]
            swizzle = "".join(ts["xyzw".index(c)] for c in dm)
            off = re.search(r"aoffimmi(?:_indexable)?\((-?\d+),(-?\d+),(-?\d+)\)", op)
            offset = "" if not off else ", int2(" + off[1] + ", " + off[2] + ")"
            assert not off or off[3] == "0"
            if op.startswith("gather4"):
                assert m["dest"] is None
                assert re.fullmatch(r"[rv]\d+\.[xyzw]{4}", uv), uv
                ur, um = uv.split(".")
                if ur.startswith("r"):
                    coord = ur.replace("r", "tmp", 1) + "." + um[:2]
                else:
                    assert ur == "v1" and um[:2] == "xy", uv
                    coord = "inp.texcoord.xy"
                component = {"x": "Red", "y": "Green", "z": "Blue", "w": "Alpha"}[
                    sampler.split(".")[1]
                ]
                value = f"{texture}.Gather{component}(sampler{texture}, {coord}{offset}).{swizzle}"
            else:
                assert (m["dest"] if "." in m["dest"] else m["dest"] + ".xyzw") == dr.replace(
                    "r", "tmp", 1
                ) + "." + dm, (p, inst, m[0])
                call = m["call"]
                assert call.startswith("tex2Dlod(" if op.startswith("sample_l") else "tex2D("), (
                    inst,
                    call,
                )
                if op.startswith("sample_l"):
                    cm = re.fullmatch(r"tex2Dlod\((\w+), float4\((.*), 0, ([^,]+)\)\)", call)
                    assert cm, call
                    assert cm[1] == texture
                    value = f"{texture}.SampleLevel(sampler{texture}, {cm[2]}, {cm[3]}{offset}).{swizzle}"
                else:
                    cm = re.fullmatch(r"tex2D\((\w+), (.*)\)", call)
                    assert cm, call
                    assert cm[1] == texture
                    value = f"{texture}.Sample(sampler{texture}, {cm[2]}{offset}).{swizzle}"
            replacement = m["indent"] + dr.replace("r", "tmp", 1) + "." + dm + " = " + value + ";"
            notes.append(
                {
                    "original_instruction": inst,
                    "recovered_before": m[0].strip(),
                    "recovered_after": replacement.strip(),
                }
            )
            return replacement

        repaired = pattern.sub(replace, s)
        for texture in ["_MainTex", "_DitheringTex"]:
            repaired = repaired.replace(
                "sampler2D " + texture + ";",
                f"Texture2D<float4> {texture}; SamplerState sampler{texture};",
            )
        predicate_writes = {}
        boolean_repairs = []
        fixed = []
        for line in repaired.splitlines(True):
            assignment = re.match(r"(\s*)(tmp\d+)(?:\.([xyzw]+))? = (.*);", line)

            def known_predicate(value):
                operand = re.fullmatch(r"(tmp\d+)\.([xyzw])", value)
                return operand and predicate_writes.get((operand[1], operand[2]), {}).get(
                    "comparison", False
                )

            if assignment:
                indent, reg, mask, rhs = assignment.groups()
                mask = mask or "xyzw"
                neg = re.fullmatch(r"~uint[1-4]\((tmp\d+)\.([xyzw]+)\)", rhs)
                if neg:
                    evidence = [predicate_writes.get((neg[1], lane)) for lane in neg[2]]
                    assert all(e and e["comparison"] for e in evidence), (rel, line, evidence)
                    replacement = (
                        indent + reg + "." + mask + " = !(" + neg[1] + "." + neg[2] + ");\n"
                    )
                    boolean_repairs.append(
                        {
                            "before": line.strip(),
                            "after": replacement.strip(),
                            "predicate_assignments": evidence,
                        }
                    )
                    line = replacement
                    rhs = "!(" + neg[1] + "." + neg[2] + ")"
                gate = re.fullmatch(r"uint1\((tmp\d+\.[xyzw])\) & uint1\((tmp\d+\.[xyzw])\)", rhs)
                if gate:
                    left, right = gate.groups()
                    lp, rp = known_predicate(left), known_predicate(right)
                    assert lp or rp, (rel, line, predicate_writes)
                    predicate, value = (left, right) if lp else (right, left)
                    replacement = (
                        indent + reg + "." + mask + " = " + predicate + " ? " + value + " : 0.0;\n"
                    )
                    boolean_repairs.append(
                        {
                            "before": line.strip(),
                            "after": replacement.strip(),
                            "predicate_mask_gate": predicate,
                        }
                    )
                    line = replacement
                    rhs = predicate + " ? " + value + " : 0.0"
                ternary = re.fullmatch(
                    r"tmp\d+\.[xyzw] \? (tmp\d+\.[xyzw]) : (tmp\d+\.[xyzw])", rhs
                )
                is_pred = bool(re.search(r" >= | <= | == | != | > | < ", rhs)) or bool(
                    ternary and known_predicate(ternary[1]) and known_predicate(ternary[2])
                )
                for lane in mask:
                    predicate_writes[(reg, lane)] = {
                        "statement": line.strip(),
                        "comparison": is_pred,
                    }
            fixed.append(line)
        repaired = "".join(fixed)
        before = hashlib.sha256(s.encode()).hexdigest()
        after = hashlib.sha256(repaired.encode()).hexdigest()
        dest = root / "shaders/critical-texture-repaired" / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(repaired)
        changes.append(
            {
                "file": rel,
                "original_program": row,
                "before_sha256": before,
                "after_sha256": after,
                "operations": notes,
                "predicate_complement_repairs": boolean_repairs,
            }
        )
        seen[rel] = True
(root / "shaders/finalpass-texture-instruction-repair.json").write_text(
    json.dumps(
        {
            "scope": "Original FinalPass pixel texture instructions only; isolated probe includes",
            "files": changes,
        },
        indent=2,
    )
)
print(
    "Repaired",
    len(changes),
    "includes",
    sum(len(r["operations"]) for r in changes),
    "original texture instructions",
)
