#!/usr/bin/env python3
"""Infer mutually exclusive keyword groups from the ORIGINAL enumerated branch sets.
All variants retained with multi_compile; reject any factorization introducing gaps.
"""

from pathlib import Path
import re, json, itertools

root = Path(__file__).resolve().parents[1]
src = root / "shaders/variant-recovery-v2/ExportedProject/Assets/Shader"
dst = root / "shaders/variants-grouped"
dst.mkdir(exist_ok=True)
rows = []


def process(block, stats):
    keywords = re.findall(r"#pragma shader_feature __ (\w+)", block)
    if not keywords:
        return block
    branches = []
    for line in re.findall(r"^\s*#(?:if|elif) (.+)$", block, re.M):
        branches.append(frozenset(re.findall(r"(?<!!)defined\((\w+)\)", line)))
    sets = set(branches)
    remain = set(keywords)
    groups = []
    # Light type is one semantic choice. Greedy pairing can otherwise combine
    # SHADOWS_DEPTH with most light types and leave SPOT in another group,
    # introducing a no-light default that the original ForwardAdd never had.
    lights = remain & {"DIRECTIONAL", "DIRECTIONAL_COOKIE", "POINT", "POINT_COOKIE", "SPOT"}
    if len(lights) > 1 and all(len(lights & b) == 1 for b in sets):
        groups.append((sorted(lights), False))
        remain -= lights
    while remain:
        seed = sorted(remain)[0]
        group = {seed}
        for k in sorted(remain - {seed}):
            if all(not any(k in b and x in b for b in sets) for x in group):
                group.add(k)
        remain -= group
        optional = any(not (group & b) for b in sets)
        groups.append((sorted(group), optional))
    product = set(
        frozenset(k for k in combo if k)
        for combo in itertools.product(*[([""] if optional else []) + g for g, optional in groups])
    )
    exact = product == sets
    stats.append(
        {
            "branches": len(sets),
            "groups": groups,
            "exact_factorization": exact,
            "synthetic_combinations": len(product - sets),
            "missing_combinations": len(sets - product),
        }
    )
    # If the original set cannot be factored without gaps, retain #error fallback; report explicitly.
    decl = "\n".join(
        "#pragma multi_compile " + ("__ " if optional else "") + " ".join(g)
        for g, optional in groups
    )
    block = re.sub(r"#pragma shader_feature __ \w+\n", "", block)
    return block.replace("#pragma target 4.5", "#pragma target 4.5\n" + decl, 1)


for p in src.glob("*.shader"):
    s = p.read_text().replace("Mode 0", "Mode Off")
    if "#pragma shader_feature" not in s:
        continue
    stats = []
    s = re.sub(
        r"CGPROGRAM(.*?)ENDCG",
        lambda m: "CGPROGRAM" + process(m.group(1), stats) + "ENDCG",
        s,
        flags=re.S,
    )
    (dst / p.name).write_text(s)
    rows.append({"file": p.name, "passes": stats})
(root / "shaders/variant-groups-report.json").write_text(json.dumps(rows, indent=2))
print(
    "Files",
    len(rows),
    "exact pass groups",
    sum(p["exact_factorization"] for r in rows for p in r["passes"]),
    "/",
    sum(len(r["passes"]) for r in rows),
)
