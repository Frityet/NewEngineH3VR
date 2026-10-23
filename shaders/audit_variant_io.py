"""Conservative syntactic varying-lane audit; not a control-flow or GPU proof."""

from pathlib import Path
import re, json, argparse

p = argparse.ArgumentParser()
p.add_argument("sources", nargs="+")
p.add_argument("--output", required=True)
args = p.parse_args()
rows = []
for path in map(Path, args.sources):
    text = path.read_text()
    branches = []
    for m in re.finditer(
        r"(?m)^\s*#(?:if|elif) ([^\n]+)\n(.*?)(?=^\s*#(?:elif|else)\b)", text, re.S
    ):
        body = m[2]
        struct = re.search(r"struct v2f\s*\{(.*?)\};", body, re.S)
        vert = re.search(r"v2f vert\([^)]*\)\s*\{(.*?)return o;", body, re.S)
        frag = re.search(r"fout frag\([^)]*\)\s*\{(.*?)return o;", body, re.S)
        if not (struct and vert and frag):
            continue
        fields = {
            x[2]: int(x[1] or "1") for x in re.finditer(r"float([1-4]?)\s+(\w+)\s*:", struct[1])
        }
        writes = {name: set() for name in fields}
        reads = {name: set() for name in fields}
        for field, lanes in re.findall(r"\bo\.(\w+)(?:\.([xyzwrgba]+))?\s*=", vert[1]):
            if field in fields:
                writes[field].update(lanes or "xyzw"[: fields[field]])
        for field, lanes in re.findall(r"\binp\.(\w+)(?:\.([xyzwrgba]+))?", frag[1]):
            if field in fields:
                reads[field].update(lanes or "xyzw"[: fields[field]])
        missing = {k: "".join(sorted(v - writes[k])) for k, v in reads.items() if v - writes[k]}
        branches.append({"keywords": m[1], "missing": missing})
    rows.append(
        {
            "source": str(path),
            "branches": len(branches),
            "issues": [dict(index=i, **b) for i, b in enumerate(branches) if b["missing"]],
        }
    )
report = {
    "limitation": "Syntactic union of assignments; does not prove every control-flow path writes each input or arithmetic/visual correctness. Ignores system-value inputs not in v2f.",
    "sources": rows,
}
Path(args.output).write_text(json.dumps(report, indent=2))
print([(r["source"], r["branches"], len(r["issues"])) for r in rows])
