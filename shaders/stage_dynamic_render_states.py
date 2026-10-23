"""Apply only proven named state restoration to current shaders, retaining variants.
Usage: python3 shaders/stage_dynamic_render_states.py unity|unity-full
"""

from pathlib import Path
import re, json, sys, hashlib, runpy

root = Path(__file__).resolve().parents[1]
project = sys.argv[1]
assert project in ("unity", "unity-full")
helpers = runpy.run_path(str(root / "shaders/repair_dynamic_render_states.py"))
patch = helpers["patch_header"]
states = helpers["states"]
rows = []
held = []
if project == "unity":
    mapping = [
        {
            "path": Path(r["target"]),
            "source_file": r["selected"]["source_file"],
            "path_id": r["selected"]["source_path_id"],
            "name": r["name"],
        }
        for r in json.load(open(root / "shaders/replacement-manifest.json"))
        if r.get("selected")
    ]
else:
    mapping = [
        {
            "path": root / "unity-full" / r["exported_path"],
            "source_file": r["original_source_file"],
            "path_id": r["path_id"],
            "name": r["name"],
        }
        for r in json.load(open(root / "build/shader-identities/full-shader-identity-map.json"))
    ]
for row in mapping:
    p = row["path"]
    text = p.read_text()
    before = text
    original = states.get((row["source_file"], row["path_id"]))
    changes = []
    if not original:
        continue
    # Public Alloy implementations already retain property-driven render states and
    # use original source syntax rather than the legacy exporter's GpuProgramID.
    if "Assets/Alloy/Shaders/" in text:
        continue
    passes = list(re.finditer(r"(?m)^\s*Pass\s*\{.*?GpuProgramID[^\n]*\n", text, re.S))
    relevant = [
        r
        for r in original["passes"]
        if any(not x["field"].startswith("fog") for x in r["references"])
    ]
    for state in reversed(relevant):
        assert state["subshader"] == 0 and state["pass"] < len(passes), (p, state["pass"])
        match = passes[state["pass"]]
        header = match[0]
        n = re.search(r'\bName\s+"([^"]+)"', header)
        assert not n or n[1].upper() == str(state["name"]).upper(), (p, n[1], state["name"])
        repaired, edits = patch(header, state["state"])
        text = text[: match.start()] + repaired + text[match.end() :]
        changes += edits
    if text != before:
        meta = p.with_suffix(".shader.meta").read_bytes()
        p.write_text(text)
        assert p.with_suffix(".shader.meta").read_bytes() == meta
        rows.append(
            {
                "target": str(p.relative_to(root)),
                "guid": re.search(rb"guid: (\w+)", meta)[1].decode(),
                "before_sha256": hashlib.sha256(before.encode()).hexdigest(),
                "sha256": hashlib.sha256(text.encode()).hexdigest(),
                "directives": changes,
                "validation": "dynamic-state-native-summary.json",
            }
        )
(root / ("shaders/staged-dynamic-state-" + project + ".json")).write_text(
    json.dumps(rows, indent=2)
)
print("Changed", len(rows), "current source files in", project)
# Current graph manifests describe installed bytes; the original passing baseline remains immutable.
manifest = root / (
    "shaders/working-shader-manifest.json"
    if project == "unity"
    else "shaders/staged-full-sources.json"
)
data = json.loads(manifest.read_text())
entries = data if isinstance(data, list) else data["shaders"]
changes = {r["target"]: r for r in rows}
for r in entries:
    key = r.get("path", r.get("target"))
    if key in changes:
        r["sha256"] = changes[key]["sha256"]
        r["render_state_validation"] = "dynamic-state-native-summary.json"
manifest.write_text(json.dumps(data, indent=2))
