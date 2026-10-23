"""Narrow, fail-closed original-DXBC IEEE magnitude-mask repair; never stage projects."""

from pathlib import Path
import hashlib, json, re

root = Path(__file__).resolve().parents[2]
out = root / "shaders/ieee-mask-repair/candidate"
m = json.loads((root / "shaders/semantic-v2-validated/manifest.json").read_text())
names = {
    "Hidden/PostProcessing/Copy",
    "Hidden/PostProcessing/CopyStd",
    "Hidden/PostProcessing/Debug/Overlays",
}
rows = [r for r in m["rows"] if r["identity"]["name"] in names]
assert len(rows) == 3
pattern = re.compile(
    r"(?P<indent>[ \t]*)tmp1 = uint4\(tmp0\) & uint4\(float4\(asfloat\(0x7FFFFFFFu\), asfloat\(0x7FFFFFFFu\), asfloat\(0x7FFFFFFFu\), asfloat\(0x7FFFFFFFu\)\)\);\n[ \t]*tmp1 = tmp1 == int4\(2139095040, 2139095040, 2139095040, 2139095040\);"
)


def digest(data):
    return hashlib.sha256(data).hexdigest()


files = {}
targets = []
for r in rows:
    p = r["identity"]["exported_path"]
    raw = (root / "unity-full" / p).read_bytes()
    assert digest(raw) == r["sha256"], p
    files[p] = raw
    for dep in re.findall(
        r'#include\s+"(Assets/NativeShaders/SemanticV2Variants/[^\"]+)"', raw.decode()
    ):
        d = (root / "unity-full" / dep).read_bytes()
        assert digest(d) == m["dependencies"][dep], dep
        files[dep] = d
    targets.append({"identity": r["identity"], "source_sha256": digest(raw), "path": p})
proof = []
for p, raw in sorted(files.items()):
    s = raw.decode()

    def fix(mt):
        i = mt["indent"]
        return (
            i
            + "uint4 nativeMagnitudeBits = asuint(tmp0) & 0x7FFFFFFFu;\n"
            + i
            + "tmp1 = nativeMagnitudeBits == 0x7F800000u;"
        )

    fixed, n = pattern.subn(fix, s)
    dest = out / p
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(fixed)
    proof.append(
        {
            "path": p,
            "before_sha256": digest(raw),
            "after_sha256": digest(dest.read_bytes()),
            "repaired_patterns": n,
        }
    )
assert sum(r["repaired_patterns"] for r in proof) == 6
assert sum(r["repaired_patterns"] for r in proof if r["path"].endswith(".shader")) == 2
assert sum(r["repaired_patterns"] for r in proof if r["path"].endswith(".cginc")) == 4
report = {
    "staged": False,
    "targets": targets,
    "files": proof,
    "patterns_repaired": 6,
    "original_semantics": "(float IEEE bits & 0x7fffffff) == 0x7f800000; infinity only, no NaN classification added",
    "limits": [
        "Native Metal pixel validation pending",
        "Not a generic integer emitter correction",
        "All unchanged declarations, states, passes, pragmas and outputs preserved",
    ],
    "original_manifest_sha256": digest(
        (root / "shaders/ieee-mask-repair/original/manifest.json").read_bytes()
    ),
}
(root / "shaders/ieee-mask-repair/candidate-manifest.json").write_text(json.dumps(report, indent=2))
print(
    "Prepared",
    len(targets),
    "shaders",
    len(files),
    "files",
    sum(r["repaired_patterns"] for r in proof),
    "exact pattern repairs",
)
