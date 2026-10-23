from pathlib import Path
import json, re, hashlib, collections

root = Path(__file__).resolve().parents[1]
source = {
    (x["copy_file"].lower(), x["copy_path_id"]): x
    for x in json.load(open(root / "audit-assets/all-shader-source-map.json"))
}
rows = []
for line in (root / "shaders/final-export-identity.tsv").read_text().splitlines():
    f, id, p = line.split("\t")
    o = source[(f.lower(), int(id))]
    p = Path(p)
    text = p.read_text()
    rows.append(
        {
            **o,
            "export_path": str(p),
            "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
            "unsupported_opcodes": sorted(set(re.findall("unsupported_(\\w+)", text))),
            "no_subprograms": text.count("No subprograms found"),
        }
    )
byname = collections.defaultdict(list)
for r in rows:
    byname[r["name"]].append(r)
replacements = []
for target in sorted((root / "unity/Assets").rglob("*.shader")):
    match = re.search(r'Shader\s+"([^"]+)"', target.read_text())
    name = match.group(1) if match else None
    candidates = [r for r in byname[name] if not r["source_file"].startswith("StreamingAssets/")]
    hashes = set(r["sha256"] for r in candidates)
    choice = candidates[0] if len(hashes) == 1 else None
    replacements.append(
        {
            "target": str(target),
            "name": name,
            "meta_guid": re.search(
                "guid: (\\w+)", target.with_suffix(".shader.meta").read_text()
            ).group(1),
            "candidate_count": len(candidates),
            "distinct_sources": len(hashes),
            "selected": choice,
            "candidate_exports": [r["export_path"] for r in candidates],
        }
    )
(root / "shaders/export-identities.json").write_text(json.dumps(rows, indent=2))
(root / "shaders/replacement-manifest.json").write_text(json.dumps(replacements, indent=2))
print(
    "Targets",
    len(replacements),
    "unique identity candidates",
    sum(r["selected"] is not None for r in replacements),
    "ambiguous",
    sum(r["distinct_sources"] > 1 for r in replacements),
    "missing",
    sum(not r["candidate_count"] for r in replacements),
)
