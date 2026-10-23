"""Factor preprocessor branches into shared includes; prove exact expanded source identity."""

from pathlib import Path
import re, json, hashlib

root = Path(__file__).resolve().parents[1]
source = root / "shaders/pip-flat-validated-compile.shader"
dest = root / "shaders/pip-packaged"
incdir = dest / "Assets/NativeShaders/PIPScopeVariants"
incdir.mkdir(parents=True, exist_ok=True)
rows = []
s = source.read_text()
index = 0
# These are the generated whole-program variant branches; inner HLSL control flow has no preprocessor else/elif.
pattern = re.compile(
    r"(?P<condition>^[ \t]*#(?:if|elif) [^\n]+\n)(?P<body>.*?)(?=^[ \t]*#(?:elif|else)\b)",
    re.M | re.S,
)


def extract(m):
    global index
    body = m["body"]
    assert "v2f vert(" in body and "fout frag(" in body
    filename = f"variant-{index:04d}.cginc"
    index += 1
    path = incdir / filename
    path.write_text(body)
    rows.append(
        {
            "file": filename,
            "condition": m["condition"].strip(),
            "sha256": hashlib.sha256(body.encode()).hexdigest(),
        }
    )
    return m["condition"] + f'#include "Assets/NativeShaders/PIPScopeVariants/{filename}"\n'


packed = pattern.sub(extract, s)
assert index == 1540
expanded = re.sub(
    r'^#include "Assets/NativeShaders/PIPScopeVariants/([^\"]+)"\n',
    lambda m: (incdir / m[1]).read_text(),
    packed,
    flags=re.M,
)
assert expanded == s
(dest / "Unlit_PIPScope.shader").write_text(packed)
report = {
    "source": str(source.relative_to(root)),
    "source_sha256": hashlib.sha256(s.encode()).hexdigest(),
    "expanded_sha256": hashlib.sha256(expanded.encode()).hexdigest(),
    "flat_bytes": len(s.encode()),
    "shader_bytes": len(packed.encode()),
    "include_count": index,
    "variants": rows,
}
(dest / "manifest.json").write_text(json.dumps(report, indent=2))
print({k: v for k, v in report.items() if k != "variants"})
