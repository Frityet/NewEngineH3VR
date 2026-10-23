"""Isolated VTP Glass typed integer repair; original helpers/proofs stay immutable."""

from pathlib import Path
import re, json, hashlib

root = Path(__file__).resolve().parents[2]
out = root / "shaders/vtp-integer-repair/candidate"
base = root / "unity-full"
source = "Assets/Shader/VTP_Glass_2 Texture blend (2-sided).shader"
manifest = json.loads((root / "shaders/semantic-v2-validated/manifest.json").read_text())
row = next(r for r in manifest["rows"] if r["identity"]["exported_path"] == source)
sha = lambda b: hashlib.sha256(b).hexdigest()
text = (base / source).read_text()
assert sha(text.encode()) == row["sha256"]
files = {source: text}
for p in set(re.findall(r'#include\s+"(Assets/NativeShaders/SemanticV2Variants/[^\"]+)"', text)):
    data = (base / p).read_bytes()
    assert sha(data) == manifest["dependencies"][p]
    files[p] = data.decode()
setup = re.compile(
    r"(?P<i>[ \t]*)(?P<sign>tmp\d+\.[xyzw]) = uint1\(_ParallaxInterpolation\) \^ uint1\(0\.0\);\n[ \t]*(?P<mag>tmp\d+\.[xyzw]) = max\(_ParallaxInterpolation, -_ParallaxInterpolation\);\n[ \t]*(?P=mag) = float1\(int1\((?P=mag)\) >> 1\);\n[ \t]*(?P<neg>tmp\d+\.[xyzw]) = -(?P=mag);\n[ \t]*(?P=sign) = uint1\((?P=sign)\) & uint1\(-0\.0\);\n[ \t]*(?P=sign) = (?P=sign) \? (?P=neg) : (?P=mag);"
)
gate = re.compile(r"(?P<dst>tmp\d+\.[xyzw]) = (?P<pred>tmp\d+\.[xyzw]) \? 0\.0 : 0\.0;")
proof = []
for path, before in sorted(files.items()):
    matches = list(setup.finditer(before))
    after = before
    loop_count = 0
    if matches:
        assert len(matches) == 3, path
        assert "int _ParallaxInterpolation;" in before, path
        # Work backwards so boundaries for the next original setup remain valid.
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
            assert len(lm) == 2, (path, k, len(lm))
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
                assert body.count(old) == 1, (path, k, n)
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
                loop_count += 1
            after = after[: mt.start()] + segment + after[end:]
        after, ng = gate.subn(lambda m: m["dst"] + " = " + m["pred"] + " ? 1.0 : 0.0;", after)
        assert ng == 3, (path, ng)
        assert not setup.search(after)
        assert loop_count == 6
    else:
        ng = 0
    dst = out / path
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(after)
    proof.append(
        {
            "path": path,
            "before_sha256": sha(before.encode()),
            "after_sha256": sha(after.encode()),
            "integer_setups": len(matches),
            "integer_gates": ng,
            "typed_loops": loop_count,
        }
    )
assert sum(bool(r["integer_setups"]) for r in proof) == 374
assert sum(r["integer_setups"] for r in proof) == 1122
result = {
    "staged": False,
    "validated": False,
    "identity": row["identity"],
    "source": source,
    "files": proof,
    "changed_files": 374,
    "typed_setups": 1122,
    "integer_gates": 1122,
    "typed_loops": 2244,
    "original_evidence": "shaders/ieee-mask-repair/original/VTP_Glass_2 Texture blend (2-sided)-114.asm",
    "limits": [
        "Native original-instruction oracle pending",
        "No broad generic emitter changes",
        "Outer original condition rejects nonpositive interpolation for actual parallax traversal; signed setup tested separately",
    ],
}
(root / "shaders/vtp-integer-repair/candidate-manifest.json").write_text(
    json.dumps(result, indent=2)
)
print("Prepared", len(proof), "files;374changed,1122setups/gates,2244typedloops")
