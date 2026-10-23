"""Bound the cached original-DXBC footprint of named-CBuffer swizzle handling.

This is an upper-bound audit: asm files are the original disassemblies already
captured by neighboring probes, but parameter metadata is only available for some
programs, so counts identify candidates rather than confirmed affected uniforms.
"""

from collections import Counter
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[2]
ASM_ROOT = ROOT / "shaders"
OUT = Path(__file__).resolve().parent / "original-bytecode-swizzle-audit.json"
LANE = {"x": 0, "y": 1, "z": 2, "w": 3}
CB = re.compile(r"cb\d+\[[^]]+\](?:\.([xyzw]+))?")

seen_hashes = set()
files = []
swizzle_counts = Counter()
candidate_counts = Counter()
unique_programs = 0
for path in sorted(ASM_ROOT.rglob("*.asm")):
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    is_new = digest not in seen_hashes
    seen_hashes.add(digest)
    if is_new:
        unique_programs += 1
    local = Counter()
    for line_no, line in enumerate(data.decode("utf-8", "replace").splitlines(), 1):
        fields = line.strip().split(None, 2)
        if len(fields) < 2 or not fields[1].endswith(","):
            continue
        destination = fields[1][:-1]
        match = re.search(r"\.([xyzw]+)$", destination)
        dest_lanes = [LANE[c] for c in match.group(1)] if match else [0, 1, 2, 3]
        for operand in CB.finditer(line):
            swizzle = operand.group(1)
            if not swizzle:
                continue
            source_lanes = [LANE[c] for c in swizzle]
            if len(source_lanes) == 4:
                mapped = [source_lanes[i] for i in dest_lanes]
            elif len(source_lanes) == 1:
                mapped = source_lanes * len(dest_lanes)
            else:
                mapped = source_lanes
            if len(mapped) != len(dest_lanes):
                continue
            if mapped == list(range(len(mapped))):
                kind = "identity"
            elif len(set(mapped)) == 1:
                kind = "broadcast"
            elif len(set(mapped)) == len(mapped):
                kind = "permutation"
            else:
                kind = "partial_repeat"
            local[kind] += 1
            swizzle_counts[kind] += 1
            # Potential suppression requires both component-width metadata to
            # equal the post-map write width and a non-identity source mapping.
            if kind != "identity" and len(mapped) in (2, 3, 4):
                candidate_counts[len(mapped)] += 1
    if is_new:
        files.append(
            {
                "path": str(path.relative_to(ROOT)),
                "sha256": digest,
                "nonidentity_cbuffer_swizzles": sum(local[k] for k in local if k != "identity"),
                "counts": dict(local),
            }
        )

OUT.write_text(
    json.dumps(
        {
            "scope": "Cached original DXBC disassemblies under shaders/; duplicates collapsed only for distinct-program count.",
            "asm_files": len(list(ASM_ROOT.rglob("*.asm"))),
            "distinct_asm_programs": unique_programs,
            "repeated_or_permuted_cbuffer_swizzle_occurrences": dict(swizzle_counts),
            "potential_count_width_matches_if_uniform_dimension_equals_destination_width": dict(
                candidate_counts
            ),
            "limitations": "Not every original shader is disassembled here, and cached asm manifests do not consistently include vector-parameter dimensions. Width matches are an upper bound, not confirmed visual defects.",
            "unique_programs": files,
        },
        indent=2,
    )
    + "\n"
)
print(
    f"Audited {unique_programs} distinct programs from {len(list(ASM_ROOT.rglob('*.asm')))} asm files; candidates by width: {dict(candidate_counts)}"
)
