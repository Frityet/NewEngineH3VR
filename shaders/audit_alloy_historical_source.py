from pathlib import Path
import json, re, subprocess

root = Path(__file__).resolve().parents[1]
repo = root / ".tools/Alloy-source"
# Reuse parser without executing the report writer.
scope = {"re": re}
code = (root / "shaders/audit_alloy_source.py").read_text()
exec(code[code.index("pattern=") : code.index("public={}")], scope)
props = scope["props"]
report = json.loads((root / "shaders/alloy-source-match.json").read_text())
rows = []
seen = set()
for row in report["shaders"]:
    if row["name"] in seen or not row["candidates"]:
        continue
    seen.add(row["name"])
    file = str(Path(row["candidates"][0]["source"]).relative_to(".tools/Alloy-source"))
    p = props((root / row["original_source"]).read_text())
    try:
        text = subprocess.check_output(
            ["git", "-C", str(repo), "show", "origin/unity-5-6:" + file],
            text=True,
            stderr=subprocess.DEVNULL,
        )
    except subprocess.CalledProcessError:
        continue
    q = props(text)
    diff = {
        k: {"original": p.get(k), "public": q.get(k)}
        for k in p.keys() | q.keys()
        if p.get(k) != q.get(k)
    }
    row = {
        "name": row["name"],
        "repository_path": file,
        "original_source": row["original_source"],
        "property_differences": diff,
        "original_property_count": len(p),
        "historical_property_count": len(q),
    }
    rows.append(row)
    print(row["name"], len(diff), list(diff))
(root / "shaders/alloy-historical-source-match.json").write_text(
    json.dumps(
        {
            "repository": report["repository"],
            "ref": "origin/unity-5-6",
            "commit": subprocess.check_output(
                ["git", "-C", str(repo), "rev-parse", "origin/unity-5-6"], text=True
            ).strip(),
            "shaders": rows,
        },
        indent=2,
    )
)
