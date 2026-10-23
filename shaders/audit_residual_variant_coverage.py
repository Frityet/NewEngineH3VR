"""Read-only ranking of selected-program sources against exact original programs."""

from pathlib import Path
import json, re, gzip, collections

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads((root / p).read_text())
materials = {r["guid"]: r for r in read("shaders/material-keyword-audit.json")["shaders"]}
source_map = {
    (r["source_file"], r["source_path_id"]): r
    for r in read("audit-assets/all-shader-source-map.json")
}
residual = {}
for row in read("shaders/staged-full-sources.json")["shaders"]:
    m = materials[row["guid"]]
    text = (root / row["target"]).read_text()
    # Flat original-program comments with no feature groups indicate the single-program exporter.
    # Fully recovered one-branch sources are distinguished by original inventory below.
    selected_current = [sorted(set(k.split())) for k in re.findall(r"// Keywords:([^\n]*)", text)]
    declared_current = re.findall(
        r"^\s*#pragma (?:multi_compile\w*|shader_feature\w*) (.+)$", text, re.M
    )
    if selected_current and not declared_current:
        original = source_map[(row["original_source_file"], row["path_id"])]
        residual[(original["copy_file"], original["copy_path_id"])] = dict(
            row,
            materials=m["materials"],
            material_keyword_sets=m["sets"],
            selected_program_keywords=selected_current,
            examples=m["examples"],
        )


def objects():
    with gzip.open(root / "shaders/original-variants.json.gz", "rt") as f:
        next(f)
        lines = []
        for line in f:
            if line.strip() == "]" and not lines:
                return
            lines.append(line)
            if line.rstrip() in ["  },", "  }"]:
                yield json.loads("".join(lines).rstrip().rstrip(","))
                lines = []


rows = []
for original in objects():
    key = (original["copy_file"], original["path_id"])
    if key not in residual:
        continue
    row = residual[key]
    programs = original["programs"]
    keywords = {k for p in programs for k in p["keywords"]}
    mono = [p for p in programs if "UNITY_SINGLE_PASS_STEREO" not in p["keywords"]]
    passes = []
    for p in original["passes"]:
        stages = {}
        for stage, info in p["programs"].items():
            items = [programs[s["m_BlobIndex"]] for s in info.get("m_SubPrograms", [])]
            sets = {tuple(sorted(i["keywords"])) for i in items}
            mono_sets = {k for k in sets if "UNITY_SINGLE_PASS_STEREO" not in k}
            if items:
                stages[stage] = {
                    "programs": len(items),
                    "keyword_sets": len(sets),
                    "mono_keyword_sets": len(mono_sets),
                    "keywords": sorted({k for ks in sets for k in ks}),
                }
        passes.append(
            {"subshader": p["subshader"], "pass": p["pass"], "name": p["name"], "stages": stages}
        )
    row.update(
        original_programs=len(programs),
        original_keyword_sets=len({tuple(sorted(p["keywords"])) for p in programs}),
        original_mono_keyword_sets=len({tuple(sorted(p["keywords"])) for p in mono}),
        original_keywords=sorted(keywords),
        original_passes=passes,
    )
    selected = {k for s in row["selected_program_keywords"] for k in s}
    material_keys = {k for s in row["material_keyword_sets"] for k in s.split()}
    row["material_keywords_with_original_programs"] = sorted(material_keys & keywords)
    row["material_keywords_absent_from_original_shader"] = sorted(material_keys - keywords)
    row["material_keywords_absent_from_selected_programs"] = sorted(
        (material_keys & keywords) - selected
    )
    row["has_original_mono_alternatives"] = any(
        s["mono_keyword_sets"] > 1 for p in passes for s in p["stages"].values()
    )
    rows.append(row)
rows.sort(key=lambda r: -r["materials"])
families = {}
for row in rows:
    f = families.setdefault(
        row["name"],
        {
            "name": row["name"],
            "materials": 0,
            "targets": [],
            "mono_alternatives": False,
            "material_keywords_absent_from_selected_programs": set(),
            "original_keywords": set(),
        },
    )
    f["materials"] += row["materials"]
    f["targets"].append(row["target"])
    f["mono_alternatives"] |= row["has_original_mono_alternatives"]
    f["material_keywords_absent_from_selected_programs"].update(
        row["material_keywords_absent_from_selected_programs"]
    )
    f["original_keywords"].update(row["original_keywords"])
for f in families.values():
    for k, v in list(f.items()):
        if isinstance(v, set):
            f[k] = sorted(v)
refs = collections.defaultdict(list)
for p in (root / "recovered/ExportedProject/Assets/Scripts").rglob("*.cs"):
    text = p.read_text(errors="replace")
    for match in re.finditer(r'"([^"\n]+)"', text):
        if match[1] in families:
            refs[match[1]].append(
                {"path": str(p.relative_to(root)), "line": text.count("\n", 0, match.start()) + 1}
            )
for f in families.values():
    f["original_managed_literal_references"] = refs[f["name"]]
report = {
    "scope": "6,517 original standalone exported materials; exact source object identity; current source files read-only. Runtime-created/embedded materials and global keyword activation are not measured. No pragma alone is not a missing-feature claim; original mono stage alternatives establish residual branch loss.",
    "families": sorted(families.values(), key=lambda r: -r["materials"]),
    "objects": rows,
}
(root / "shaders/residual-variant-coverage.json").write_text(json.dumps(report, indent=2))
for r in report["families"]:
    if r["materials"]:
        print(
            r["materials"],
            r["name"],
            "mono_alternatives=",
            r["mono_alternatives"],
            "missing_material_keys=",
            r["material_keywords_absent_from_selected_programs"],
        )
print(
    "Objects", len(rows), "families", len(families), "materials", sum(r["materials"] for r in rows)
)
