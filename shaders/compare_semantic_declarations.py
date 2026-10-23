"""Assert the second frozen export changes only metadata-backed texture declarations."""

from pathlib import Path
import re, json, hashlib, difflib

root = Path(__file__).resolve().parents[1]
old = root / "shaders/semantic-v2-packaged"
new = root / "shaders/semantic-v2-shadow-packaged"
a = json.loads((old / "manifest.json").read_text())
b = json.loads((new / "manifest.json").read_text())
lookup = {r["identity"]["guid"]: r for r in b["rows"]}
mapping = {}
changes = []
inc = re.compile(r'#include "(Assets/NativeShaders/SemanticV2Variants/[^\"]+)"')
shadow = re.compile(
    r"^[ \t]*#if !defined\(SHADOW_COLLECTOR_PASS\) \|\| defined\(SHADOWMAPSAMPLER_DEFINED\)\n[ \t]*Texture\w+<float4?> _ShadowMapTexture; Sampler(?:Comparison)?State sampler_ShadowMapTexture;\n[ \t]*#endif\n",
    re.M,
)


def sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def check(x, y, label):
    if x == y:
        return
    if x != shadow.sub("", y):
        raise AssertionError(
            label
            + "\n"
            + "".join(difflib.unified_diff(x.splitlines(True), y.splitlines(True)))[:6000]
        )
    changes.append(
        {"file": label, "before": sha(x), "after": sha(y), "added_declarations": shadow.findall(y)}
    )


for r in a["rows"]:
    q = lookup[r["identity"]["guid"]]
    assert r["status"] == q["status"]
    if not r["status"].startswith("candidate"):
        continue
    x = (root / r["source"]).read_text()
    y = (root / q["source"]).read_text()
    # Graph-colouring normalization uses unordered sets. Retain the previous proven
    # pragma grouping after verifying the original guarded branch conditions match.
    xp = re.findall(r"CGPROGRAM(.*?)ENDCG", x, re.S)
    yp = list(re.finditer(r"CGPROGRAM(.*?)ENDCG", y, re.S))
    assert len(xp) == len(yp)
    pragma = re.compile(r"^[ \t]*#pragma multi_compile[^\n]*\n", re.M)
    for oldbody, m in reversed(list(zip(xp, yp))):
        newbody = m[1]
        pa = pragma.findall(oldbody)
        pb = pragma.findall(newbody)
        if pa != pb:
            assert set(" ".join(pa).split()) == set(" ".join(pb).split())
            assert re.findall(r"^\s*#(?:if|elif) .+$", oldbody, re.M) == re.findall(
                r"^\s*#(?:if|elif) .+$", newbody, re.M
            )
            pos = pragma.search(newbody).start()
            newbody = pragma.sub("", newbody)
            newbody = newbody[:pos] + "".join(pa) + newbody[pos:]
            y = y[: m.start(1)] + newbody + y[m.end(1) :]
    if (root / q["source"]).read_text() != y:
        (root / q["source"]).write_text(y)
        q["sha256"] = sha(y)
    xs = inc.findall(x)
    ys = inc.findall(y)
    assert len(xs) == len(ys)
    check(
        inc.sub('#include "SEMANTIC_VARIANT"', x),
        inc.sub('#include "SEMANTIC_VARIANT"', y),
        Path(q["source"]).name,
    )
    for p, s in zip(xs, ys):
        if p in mapping:
            assert mapping[p] == s
        else:
            check((old / p).read_text(), (new / s).read_text(), p)
            mapping[p] = s
(new / "manifest.json").write_text(json.dumps(b, indent=2))
report = {
    "passed": True,
    "old_manifest_sha256": sha((old / "manifest.json").read_text()),
    "new_manifest_sha256": sha((new / "manifest.json").read_text()),
    "changed_files": changes,
    "include_remap": {p: s for p, s in mapping.items() if p != s},
    "finalpass_unchanged": all(
        r["sha256"] == lookup[r["identity"]["guid"]]["sha256"]
        for r in a["rows"]
        if r["identity"]["name"] == "Hidden/PostProcessing/FinalPass"
    ),
}
(root / "shaders/semantic-v2-declaration-only-proof.json").write_text(json.dumps(report, indent=2))
print(
    "PASS",
    len(changes),
    "declaration-only files,",
    len(report["include_remap"]),
    "include remaps; FinalPass unchanged",
    report["finalpass_unchanged"],
)
