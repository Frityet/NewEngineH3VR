"""Rank original material features which the current recovered program cannot select.
Does not count global runtime keywords, unknown material keywords may be stale.
"""

from pathlib import Path
from collections import Counter, defaultdict
import json, re

root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / "shaders/staged-full-sources.json").read_text())["shaders"]
by_guid = {r["guid"]: r for r in manifest}
groups = {}
materials = 0
external = Counter()
for r in manifest:
    text = (root / r["target"]).read_text()
    pragmas = set()
    for line in re.findall(r"^\s*#pragma (?:multi_compile\w*|shader_feature\w*) (.+)$", text, re.M):
        pragmas.update(line.split())
    # Shader property toggle keywords prove intended features; material keyword lists can also retain obsolete entries.
    features = set(re.findall(r"\[Toggle\s*\(\s*(\w+)\s*\)\]", text))
    selected = [sorted(set(k.split())) for k in re.findall(r"// Keywords:([^\n]*)", text)]
    groups[r["guid"]] = {
        "name": r["name"],
        "target": r["target"],
        "guid": r["guid"],
        "materials": 0,
        "sets": Counter(),
        "keywords": Counter(),
        "declared_keywords": sorted(pragmas),
        "toggle_keywords": sorted(features),
        "selected_program_keywords": selected,
        "examples": [],
    }
# Main working assets may be native binary after compaction; use pristine YAML
# with identical preserved material/shader GUIDs for original keyword intent.
for p in (root / "recovered-full/ExportedProject/Assets").rglob("*.mat"):
    text = p.read_text(errors="replace")
    m = re.search(r"m_Shader: \{[^}]*guid: (\w+)", text)
    if not m:
        continue
    materials += 1
    if m[1] not in groups:
        external[m[1]] += 1
        continue
    group = groups[m[1]]
    group["materials"] += 1
    match = re.search(r"^\s*m_ShaderKeywords:([^\n]*)", text, re.M)
    keys = tuple(sorted((match[1] if match else "").strip().split()))
    group["sets"][" ".join(keys)] += 1
    group["keywords"].update(keys)
    if len(group["examples"]) < 3:
        group["examples"].append(str(p.relative_to(root)))
for g in groups.values():
    g["unselectable_toggle_keywords"] = {
        k: n
        for k, n in g["keywords"].items()
        if k in g["toggle_keywords"] and k not in g["declared_keywords"]
    }
    g["materials_using_unselectable_toggle"] = sum(
        n
        for key, n in g["sets"].items()
        if set(key.split()) & g["unselectable_toggle_keywords"].keys()
    )
    g["sets"] = dict(g["sets"].most_common())
    g["keywords"] = dict(g["keywords"].most_common())
rows = sorted(groups.values(), key=lambda g: g["materials_using_unselectable_toggle"], reverse=True)
report = {
    "material_source": "recovered-full/ExportedProject/Assets (pristine exported YAML; working graph may be binary)",
    "shader_source": "current unity-full shader sources",
    "materials": materials,
    "mapped_shader_materials": sum(g["materials"] for g in rows),
    "other_shader_materials": sum(external.values()),
    "limitation": "Counts serialized material keywords only, not runtime/global keywords. A missing pragma proves no alternate program selection, but does not establish a feature is absent from every selected baseline program. Toggle keywords are used to exclude stale unrelated material keywords.",
    "shaders": rows,
}
(root / "shaders/material-keyword-audit.json").write_text(json.dumps(report, indent=2))
print({k: v for k, v in report.items() if k != "shaders"})
for r in rows[:12]:
    print(
        r["name"],
        r["materials"],
        r["materials_using_unselectable_toggle"],
        r["unselectable_toggle_keywords"],
    )
