"""Rebuild hash-anchored IEEE/VTP source overlays for a corrected-emitter candidate.

The manifest records exact input/output hashes and structural rewrite counts. Those
checks make this transformation reproducible; they are not per-file runtime or
pixel-equivalence proofs. The native pixel proof covers the separate corrected HDR
LUT path only, as stated in the emitted manifest's limits.
"""

from pathlib import Path
import hashlib, json, re

root = Path(__file__).resolve().parents[2]
base = root / "shaders/color-path/semantic-v3-packaged"
out = root / "shaders/color-path/semantic-v3-overlays"
assert base.exists() and not out.exists()
manifest = json.loads((base / "manifest.json").read_text())
rows = {r["identity"]["name"]: r for r in manifest["rows"]}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def load_top(name):
    r = rows[name]
    p = Path(r["source"])
    if not p.is_absolute():
        p = root / p
    data = p.read_bytes()
    assert digest(data) == r["sha256"], (p, "manifest mismatch")
    return r, p, data.decode()


def deps(text):
    return sorted(
        set(re.findall(r'#include\s+"(Assets/NativeShaders/SemanticV2Variants/[^"]+)"', text))
    )


def put(rel, text, before_sha, proof):
    p = out / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text)
    proof.append({"path": rel, "before_sha256": before_sha, "after_sha256": digest(p.read_bytes())})


# IEEE infinity-mask repairs. The pattern/count checks below verify the expected
# source forms and replacements; they do not substitute for backend runtime tests.
ieee_names = {
    "Hidden/PostProcessing/Copy",
    "Hidden/PostProcessing/CopyStd",
    "Hidden/PostProcessing/Debug/Overlays",
}
pattern = re.compile(
    r"(?P<indent>[ \t]*)tmp1 = uint4\(tmp0\) & uint4\(float4\(asfloat\(0x7FFFFFFFu\), asfloat\(0x7FFFFFFFu\), asfloat\(0x7FFFFFFFu\), asfloat\(0x7FFFFFFFu\)\)\);\n[ \t]*tmp1 = tmp1 == int4\(2139095040, 2139095040, 2139095040, 2139095040\);"
)
ieee_proof = []
seen = {}
for name in sorted(ieee_names):
    r, p, text = load_top(name)
    files = {p.name: (f"Assets/Shader/{p.name}", text, digest(text.encode()))}
    for dep in deps(text):
        src = base / dep
        d = src.read_bytes()
        expected = manifest["dependencies"][dep]
        assert digest(d) == expected, (src, "dependency hash mismatch")
        files[Path(dep).name] = (dep, d.decode(), expected)
    for key, (rel, source, before_sha) in files.items():

        def repair(m):
            i = m["indent"]
            return (
                i
                + "uint4 nativeMagnitudeBits = asuint(tmp0) & 0x7FFFFFFFu;\n"
                + i
                + "tmp1 = nativeMagnitudeBits == 0x7F800000u;"
            )

        fixed, n = pattern.subn(repair, source)
        if n:
            put(rel, fixed, before_sha, ieee_proof)
        seen[(name, key)] = n
assert sum(seen.values()) == 6, seen
# VTP parallax repair: integer temporaries encode the intended bit-level wraparound
# and sign operations. The regex/count assertions below are structural checks only.
r, p, text = load_top("VTP/Glass/2 Texture blend (2-sided)")
files = {}
for dep in deps(text):
    d = (base / dep).read_bytes()
    assert digest(d) == manifest["dependencies"][dep], dep
    files[dep] = d.decode()
setup = re.compile(
    r"(?P<i>[ \t]*)(?P<sign>tmp\d+\.[xyzw]) = uint1\(_ParallaxInterpolation\) \^ uint1\(0\.0\);\n[ \t]*(?P<mag>tmp\d+\.[xyzw]) = max\(_ParallaxInterpolation, -_ParallaxInterpolation\);\n[ \t]*(?P=mag) = float1\(int1\((?P=mag)\) >> 1\);\n[ \t]*(?P<neg>tmp\d+\.[xyzw]) = -(?P=mag);\n[ \t]*(?P=sign) = uint1\((?P=sign)\) & uint1\(-0\.0\);\n[ \t]*(?P=sign) = (?P=sign) \? (?P=neg) : (?P=mag);"
)
gate = re.compile(r"(?P<dst>tmp\d+\.[xyzw]) = (?P<pred>tmp\d+\.[xyzw]) \? 0\.0 : 0\.0;")
vtp_proof = []
for dep, before in sorted(files.items()):
    matches = list(setup.finditer(before))
    after = before
    loops = 0
    if matches:
        assert len(matches) == 3, (dep, len(matches))
        assert "int _ParallaxInterpolation;" in before, dep
        for k, mt in reversed(list(enumerate(matches))):
            end = matches[k + 1].start() if k + 1 < len(matches) else len(before)
            segment = before[mt.start() : end]
            i = mt["i"]
            prefix = "nativeParallax" + str(k)
            lines = [
                f"uint {prefix}Sign = asuint(_ParallaxInterpolation) ^ 2u;",
                f"int {prefix}Magnitude = max(_ParallaxInterpolation, asint(0u - asuint(_ParallaxInterpolation)));",
                f"uint {prefix}Half = asuint({prefix}Magnitude) >> 1;",
                f"{mt['mag']} = (float){prefix}Half;",
                f"{mt['neg']} = (float)asint(0u - {prefix}Half);",
                f"int {prefix}SignedHalf = ({prefix}Sign & 0x80000000u) != 0u ? asint(0u - {prefix}Half) : asint({prefix}Half);",
                f"{mt['sign']} = (float){prefix}SignedHalf;",
            ]
            segment = i + ("\n" + i).join(lines) + segment[mt.end() - mt.start() :]
            loop = re.compile(
                r"(?P<i>[ \t]*)while \(true\) \{\n(?P<j>[ \t]*)(?P<cmp>tmp\d+\.[xyzw]) = (?P<counter>tmp\d+\.[xyzw]) >= (?P<limit>_ParallaxInterpolation|"
                + re.escape(mt["sign"])
                + r");"
            )
            lm = list(loop.finditer(segment))
            assert len(lm) == 2, (dep, k, len(lm))
            for n, lp in reversed(list(enumerate(lm))):
                start = lp.start()
                brace = segment.index("{", start)
                depth = 1
                finish = brace + 1
                while depth:
                    depth += (segment[finish] == "{") - (segment[finish] == "}")
                    finish += 1
                body = segment[lp.end() : finish]
                counter = prefix + "Counter" + str(n)
                old = lp["counter"] + " = " + lp["counter"] + " + 1;"
                assert body.count(old) == 1, (dep, k, n)
                body = body.replace(
                    old,
                    counter
                    + " = asint(asuint("
                    + counter
                    + ") + 1u);\n"
                    + lp["j"]
                    + lp["counter"]
                    + " = (float)"
                    + counter
                    + ";",
                )
                limit = (
                    "_ParallaxInterpolation"
                    if lp["limit"] == "_ParallaxInterpolation"
                    else prefix + "SignedHalf"
                )
                replacement = (
                    lp["i"]
                    + "int "
                    + counter
                    + " = 0;\n"
                    + lp["i"]
                    + "while (true) {\n"
                    + lp["j"]
                    + lp["cmp"]
                    + " = "
                    + counter
                    + " >= "
                    + limit
                    + ";"
                    + body
                )
                segment = segment[:start] + replacement + segment[finish:]
                loops += 1
            after = after[: mt.start()] + segment + after[end:]
        after, ng = gate.subn(lambda m: m["dst"] + " = " + m["pred"] + " ? 1.0 : 0.0;", after)
        assert ng == 3, (dep, ng)
        assert not setup.search(after)
        assert loops == 6
    else:
        ng = 0
    if after != before:
        put(dep, after, digest(before.encode()), vtp_proof)
assert len(vtp_proof) == 374 and all(x["path"].endswith(".cginc") for x in vtp_proof)
assert len({x["path"] for x in vtp_proof}) == len(vtp_proof), "duplicate overlay paths"
report = {
    "staged": False,
    "base_manifest_sha256": digest((base / "manifest.json").read_bytes()),
    "ieee": {
        "files": ieee_proof,
        "repairs": sum(seen.values()),
        "original_semantics": "(bits & 0x7fffffff) == 0x7f800000; infinity only",
    },
    "vtp": {
        "files": vtp_proof,
        "changed_files": len(vtp_proof),
        "setups": sum(len(setup.findall(files[x])) for x in files),
        "typed_loops": sum(6 for x in files if len(setup.findall(files[x])) == 3),
    },
    "limits": [
        "This overlay candidate has not been integrated into Unity projects",
        "Native proof applies to the corrected HDR LUT shader path, not these semantic overlays",
    ],
}
(out / "overlay-manifest.json").write_text(json.dumps(report, indent=2))
print(
    "IEEE",
    len(ieee_proof),
    "files",
    report["ieee"]["repairs"],
    "repairs; VTP",
    len(vtp_proof),
    "files",
)
