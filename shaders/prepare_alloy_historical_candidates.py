"""Extract exact original-era source branch into isolated namespace, never game projects."""

from pathlib import Path
import json, re, subprocess

root = Path(__file__).resolve().parents[1]
repo = root / ".tools/Alloy-source"
ref = "origin/unity-5-6"
get = lambda f: subprocess.check_output(["git", "-C", str(repo), "show", ref + ":" + f]).decode(
    "utf-8", errors="replace"
)
files = subprocess.check_output(
    ["git", "-C", str(repo), "ls-tree", "-r", "--name-only", ref], text=True
).splitlines()
dep = root / "shaders/alloy-historical-candidates/Assets/AlloyUnity56"
for f in files:
    if f.endswith(".cginc") or f == "LICENSE.txt":
        text = get(f).replace("Assets/Alloy/", "Assets/AlloyUnity56/")
        p = dep / f
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)
        p = root / "alloy-probe/Assets/AlloyUnity56" / f
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)
match = json.loads((root / "shaders/alloy-historical-source-match.json").read_text())
audit = json.loads((root / "shaders/material-keyword-audit.json").read_text())["shaders"]
rows = []
lines = []
for name in ["Alloy/TriPlanar", "Alloy/Directional Blend", "Alloy/Vertex Blend", "Alloy/Car Paint"]:
    row = next(r for r in match["shaders"] if r["name"] == name)
    assert not row["property_differences"]
    filename = name.replace("/", "_") + ".shader"
    p = root / "shaders/alloy-historical-candidates" / filename
    text = get(row["repository_path"]).replace("Assets/Alloy/", "Assets/AlloyUnity56/")
    p.write_text(text)
    probe = root / "alloy-probe/Assets/Resources/Shaders" / filename
    probe.write_text(text)
    keys = {k for r in audit if r["name"] == name for k in r["sets"]}
    keys.add("")
    if name != "Alloy/Car Paint":
        keys.add(
            "_DIRECTIONALBLENDMODE_WORLD"
            if name == "Alloy/Directional Blend"
            else "_TRIPLANARMODE_WORLD"
        )
    for key in sorted(keys):
        lines.append(filename[:-7] + "|" + key)
    rows.append(
        {
            "name": name,
            "source": str(p.relative_to(root)),
            "probe_source": str(probe.relative_to(root)),
            "repository_path": row["repository_path"],
            "keyword_sets": sorted(keys),
            "property_abi_exact": True,
        }
    )
(root / "shaders/alloy-historical-candidates.json").write_text(
    json.dumps({"commit": match["commit"], "shaders": rows}, indent=2)
)
# Historical candidates replace the first three compat shader sources, so retain only terrain compat list.
p = root / "alloy-probe/Assets/Resources/AlloyCompatKeywords.txt"
existing = p.read_text().splitlines()
p.write_text(
    "\n".join(
        [
            l
            for l in existing
            if not any(
                l.startswith(n.replace("/", "_") + "|")
                for n in ["Alloy/TriPlanar", "Alloy/Directional Blend", "Alloy/Vertex Blend"]
            )
        ]
        + lines
    )
    + "\n"
)
print(len(rows), "historical shaders", len(lines), "material keyword sets")
