from pathlib import Path
import json, re, subprocess

root = Path(__file__).resolve().parents[1]
repo = root / ".tools/Alloy-source"
manifest = json.loads((root / "shaders/staged-full-sources.json").read_text())["shaders"]
pattern = r'^\s*(?:\[[^\n]*?\]\s*)*(\w+)\s*\(\s*"([^"\n]*)"\s*,\s*(.+)\)\s*=\s*([^\n]+)'


def props(text):
    result = {}
    for name, display, kind, value in re.findall(pattern, text, re.M):
        kind = re.sub(r"\s+", "", kind)
        value = value.split("//")[0].strip()
        try:
            if value.startswith("("):
                vals = [float(v) for v in value.strip("() ").split(",")]
                if kind == "Color" and len(vals) == 3:
                    vals.append(1.0)
                value = vals
            else:
                value = float(value)
        except ValueError:
            value = re.sub(r"\s+", "", value)
        result[name] = {"description": display, "type": kind, "default": value}
    return result


public = {}
for path in repo.rglob("*.shader"):
    text = path.read_text()
    m = re.search(r'Shader\s+"([^"]+)"', text)
    if m:
        public.setdefault(m[1], []).append(path)
rows = []
for row in manifest:
    if "alloy" not in row["name"].lower():
        continue
    original = (root / row["source"]).read_text()
    p = props(original)
    candidates = []
    for path in public.get(row["name"], []):
        text = path.read_text()
        q = props(text)
        diff = {
            k: {"original": p.get(k), "public": q.get(k)}
            for k in p.keys() | q.keys()
            if p.get(k) != q.get(k)
        }
        candidates.append(
            {
                "source": str(path.relative_to(root)),
                "property_count": len(q),
                "property_differences": diff,
                "pass_names": re.findall(r'\bName\s+"([^"]+)"', text),
            }
        )
    rows.append(
        {
            "name": row["name"],
            "target": row["target"],
            "guid": row["guid"],
            "original_source": row["source"],
            "original_property_count": len(p),
            "original_pass_names": re.findall(r'\bName\s+"([^"]+)"', original),
            "candidates": candidates,
        }
    )
report = {
    "repository": "https://github.com/Josh015/Alloy",
    "commit": subprocess.check_output(
        ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True
    ).strip(),
    "license": "MIT, Copyright (c) 2013 RUST LLC",
    "limitation": "Property ABI and pass-name comparison is necessary but does not establish exact historical arithmetic or bytecode equivalence.",
    "shaders": rows,
}
(root / "shaders/alloy-source-match.json").write_text(json.dumps(report, indent=2))
seen = set()
for r in rows:
    if r["name"] in seen:
        continue
    seen.add(r["name"])
    print(
        r["name"],
        r["original_property_count"],
        [
            (
                c["source"],
                len(c["property_differences"]),
                c["pass_names"] == r["original_pass_names"],
            )
            for c in r["candidates"]
        ],
    )
