from pathlib import Path
import re, json, hashlib, shutil

root = Path(__file__).resolve().parents[1]
dest = root / "shaders/cliff-packaged"
inc = dest / "Assets/NativeShaders/CliffVariants"
inc.mkdir(parents=True, exist_ok=True)
rows = []
shaderrows = []
unique = {}
pattern = re.compile(
    r"(?P<condition>^[ \t]*#(?:if|elif) [^\n]+\n)(?P<body>.*?)(?=^[ \t]*#(?:elif|else)\b)",
    re.M | re.S,
)
for source in sorted((root / "shaders/cliff-variants").glob("*.shader")):
    s = source.read_text()
    variants = []

    def extract(m):
        body = m["body"]
        assert "v2f vert(" in body and "fout frag(" in body
        sha = hashlib.sha256(body.encode()).hexdigest()
        filename = sha + ".cginc"
        if sha not in unique:
            (inc / filename).write_text(body)
            unique[sha] = len(body)
        variants.append({"condition": m["condition"].strip(), "sha256": sha})
        return m["condition"] + f'#include "Assets/NativeShaders/CliffVariants/{filename}"\n'

    packed = pattern.sub(extract, s)
    expanded = re.sub(
        r'^#include "Assets/NativeShaders/CliffVariants/([^\"]+)"\n',
        lambda m: (inc / m[1]).read_text(),
        packed,
        flags=re.M,
    )
    assert expanded == s
    (dest / source.name).write_text(packed)
    (root / "cliff-probe/Assets/Resources/Shaders" / source.name).write_text(packed)
    programs = list(re.finditer(r"CGPROGRAM(.*?)ENDCG", packed, re.S))
    passnames = re.findall(r'\bName\s+"([^"]+)"', packed)
    assert len(passnames) == len(programs)
    passes = []
    for i, m in enumerate(programs):
        keys = [
            " ".join(sorted(re.findall(r"(?<!!)defined\((\w+)\)", condition)))
            for condition in re.findall(r"^\s*#(?:if|elif) (.+)$", m[1], re.M)
        ]
        allkeys = sorted({k for row in keys for k in row.split()})
        passes.append(
            {"passName": passnames[i], "index": i, "keywordNames": allkeys, "allowed": keys}
        )
    shaderrows.append(
        {"file": source.name, "name": re.search(r'Shader "([^"]+)"', s)[1], "passes": passes}
    )
    rows.append(
        {
            "file": source.name,
            "flat_sha256": hashlib.sha256(s.encode()).hexdigest(),
            "packed_sha256": hashlib.sha256(packed.encode()).hexdigest(),
            "expanded_sha256": hashlib.sha256(expanded.encode()).hexdigest(),
            "variants": len(variants),
        }
    )
probeinc = root / "cliff-probe/Assets/NativeShaders/CliffVariants"
probeinc.mkdir(parents=True, exist_ok=True)
for p in inc.glob("*.cginc"):
    shutil.copyfile(p, probeinc / p.name)
report = {
    "shaders": rows,
    "shared_include_count": len(unique),
    "shared_include_bytes": sum(unique.values()),
}
(dest / "manifest.json").write_text(json.dumps(report, indent=2))
(inc / "whitelist.json").write_text(json.dumps({"shaders": shaderrows}, indent=2))
print(report)
