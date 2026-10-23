"""Update idle isolated probes using asserted declaration-only re-export deltas."""

from pathlib import Path
import re, json, hashlib

root = Path(__file__).resolve().parents[1]
proof = json.loads((root / "shaders/semantic-v2-declaration-only-proof.json").read_text())
assert proof["passed"]
old = root / "shaders/semantic-v2-packaged"
new = root / "shaders/semantic-v2-shadow-packaged"
remap = proof["include_remap"]
rows = []


def sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def upgraded(s):
    for a, b in [
        ("glstate_matrix_projection", "UNITY_MATRIX_P"),
        ("glstate_matrix_modelview0", "UNITY_MATRIX_MV"),
        ("glstate_matrix_mvp", "UNITY_MATRIX_MVP"),
    ]:
        s = s.replace(a, b)
    return s


for batch in ["semantic-materials", "semantic-optics"]:
    probe = root / (batch + "-probe")
    files = []
    for p, q in remap.items():
        actual = probe / p
        if not actual.exists():
            continue
        before = actual.read_text()
        base = (old / p).read_text()
        after = (new / q).read_text()
        if before != base:
            u = upgraded(base)
            assert before.endswith(u), p
            prefix = before[: -len(u)]
            assert not re.sub(r"// Upgrade NOTE:[^\n]*\n|\s+", "", prefix), p
            after = prefix + upgraded(after)
        dest = probe / q
        assert not dest.exists(), dest
        dest.write_text(after)
        files.append({"from": p, "to": q, "before": sha(before), "after": sha(after)})
    for p in (probe / "Assets/Resources").rglob("*.shader"):
        before = p.read_text()
        after = re.sub(
            r'#include "(Assets/NativeShaders/SemanticV2Variants/[^\"]+)"',
            lambda m: '#include "' + remap.get(m[1], m[1]) + '"',
            before,
        )
        if after != before:
            p.write_text(after)
            files.append(
                {
                    "from": str(p.relative_to(probe)),
                    "to": str(p.relative_to(probe)),
                    "before": sha(before),
                    "after": sha(after),
                }
            )
    rows.append({"project": str(probe.relative_to(root)), "files": files})
    print(batch, "updated", len(files), "files")
(root / "shaders/semantic-v2-probe-declaration-merge.json").write_text(
    json.dumps(
        {
            "passed": True,
            "declaration_proof_sha256": sha(
                (root / "shaders/semantic-v2-declaration-only-proof.json").read_text()
            ),
            "projects": rows,
        },
        indent=2,
    )
)
