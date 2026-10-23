"""Merge corrected recovered program bodies into preserved current ShaderLab state.
No Unity-project mutation. Original pass order, properties and fixed render-state headers
are retained; newly named fallback passes are recorded. Native proofs are still required.
"""

from pathlib import Path
import re, json, hashlib, itertools, argparse
from repair_stereo_matrix_indices import repair

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", default="semantic-v2-packaged")
parser.add_argument(
    "--recovery", default="semantic-v2-recovery", help="recovery directory relative to shaders/"
)
parser.add_argument(
    "--identities",
    default="semantic-v2-export-identities.tsv",
    help="identity TSV relative to shaders/",
)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
selection = json.loads((root / "shaders/semantic-v2-selection.json").read_text())
lookup = {(r["copy_file"].casefold(), r["copy_path_id"]): r for r in selection["objects"]}
out = root / "shaders" / args.output
recovery = root / "shaders" / args.recovery
identities = root / "shaders" / args.identities
out.mkdir(exist_ok=True)
inc = out / "Assets/NativeShaders/SemanticV2Variants"
inc.mkdir(parents=True, exist_ok=True)
validated = json.loads((root / "shaders/semantic-v2-validated/manifest.json").read_text())
validated_rows = {r["identity"]["exported_path"]: r for r in validated["rows"]}
scope = {"re": re, "itertools": itertools}
code = (root / "shaders/normalize_variant_groups.py").read_text()
exec(code[code.index("def process(") : code.index("for p in src.glob")], scope)
branch = re.compile(
    r"(?P<condition>^[ \t]*#(?:if|elif) [^\n]+\n)(?P<body>.*?)(?=^[ \t]*#(?:elif|else)\b)",
    re.M | re.S,
)
rows = []
held = []
dependencies = {}


def blocks(s, token):
    for m in re.finditer(r"\b" + token + r"\s*\{", s):
        i = m.end()
        d = 1
        while d:
            d += (s[i] == "{") - (s[i] == "}")
            i += 1
        yield m.start(), i, s[m.start() : i]


def keys(body):
    return {
        tuple(sorted(re.findall(r"(?<!!)defined\((\w+)\)", cond)))
        for cond in re.findall(r"^\s*#(?:if|elif) (.+)$", body, re.M)
    }


for line in identities.read_text().splitlines():
    copy, pid, exported = line.split("\t")
    r = lookup[(copy.casefold(), int(pid))]
    current = root / "unity-full" / r["exported_path"]
    current_bytes = current.read_bytes()
    current_sha = hashlib.sha256(current_bytes).hexdigest()
    # Some selected sources were already promoted into semantic-v2-validated after the
    # frozen selection was authored. Accept that exact recorded source hash as baseline.
    expected = {r["current_sha256"]}
    v = validated_rows.get(r["exported_path"])
    if v:
        expected.add(v["sha256"])
    # The IEEE proof candidate is an additive, hash-recorded repair to two selected
    # post-processing sources that were promoted after the selection snapshot.
    ieee_manifest = root / "shaders/ieee-mask-repair/candidate-manifest.json"
    if ieee_manifest.exists():
        ieee = json.loads(ieee_manifest.read_text())
        expected.update(x["after_sha256"] for x in ieee["files"] if x["path"] == r["exported_path"])
    assert current_sha in expected, (current, current_sha, expected)
    before = current_bytes.decode()
    if not r["current_recovered_hlsl"]:
        rows.append({"identity": r, "status": "preserve_current_source"})
        continue
    raw = Path(exported)
    if not raw.is_absolute():
        raw = recovery / "ExportedProject" / raw
    if not raw.exists():
        raw = recovery / "ExportedProject/Assets/Shader" / Path(exported).name
    source = raw.read_text()
    gather_repairs = []
    if r["name"] == "Hidden/PostProcessing/FinalPass":
        from restore_finalpass_gathers import restore

        source, gather_repairs = restore(source)
    assert re.search(r'Shader "([^"]+)"', source)[1] == r["name"]
    stats = []
    source = re.sub(
        r"CGPROGRAM(.*?)ENDCG",
        lambda m: "CGPROGRAM" + scope["process"](m[1], stats) + "ENDCG",
        source,
        flags=re.S,
    )
    source, stereo = repair(source, preserve_unchanged_scopes=True)
    if "unsupported_" in source:
        held.append(
            {
                "identity": r,
                "reason": "unimplemented original instruction",
                "markers": sorted(set(re.findall(r"unsupported_\w+", source))),
                "raw_source": str(raw.relative_to(root)),
            }
        )
        continue
    old = list(re.finditer(r"CGPROGRAM(.*?)ENDCG", before, re.S))
    new = list(re.finditer(r"CGPROGRAM(.*?)ENDCG", source, re.S))
    assert len(old) == len(new), (r["name"], len(old), len(new))
    programs = []
    counts = []
    for index, (a, b) in enumerate(zip(old, new)):
        body = b[1]
        oldkeys = keys(a[1])
        newkeys = keys(body)
        assert not oldkeys or oldkeys <= newkeys, (
            r["name"],
            index,
            "lost original keys",
            oldkeys - newkeys,
        )
        body = re.sub(r"^[ \t]*#pragma (vertex vert|fragment frag)[ \t]*\n", "", body, flags=re.M)
        body = "\n#pragma vertex vert\n#pragma fragment frag\n" + body
        if r["name"] in ("Unlit/Laser", "Hidden/PIPScope/PostProcess"):
            body, n = re.subn(
                r"float3 (\w+) : (SV_TARGET\d*);", r"float4 \1 : \2;", body, flags=re.I
            )
            if n:
                body = body.replace("fout o;", "fout o; o.sv_target.w = 0.0;")
        if "CBUFFER_START(UnityInstancing_PerDraw0)" in body:
            body = re.sub(r"CBUFFER_START\(UnityDrawCallInfo\).*?CBUFFER_END", "", body, flags=re.S)
            body = re.sub(
                r"CBUFFER_START\(UnityInstancing_PerDraw0\).*?CBUFFER_END", "", body, flags=re.S
            )
            body = body.replace("v.sv_instanceid.x", "v.instanceID")
            body = re.sub(
                r"unity_ObjectToWorldArray\[([^\]]+)\]",
                r"unity_Builtins0Array[\1].unity_ObjectToWorldArray",
                body,
            )
            body = re.sub(
                r"unity_WorldToObjectArray\[([^\]]+)\]",
                r"unity_Builtins0Array[\1].unity_WorldToObjectArray",
                body,
            )
            body = body.replace(
                "v2f vert(appdata_full v)\n\t\t\t{",
                "v2f vert(appdata_full v)\n\t\t\t{\n UNITY_SETUP_INSTANCE_ID(v);",
            )

        def pack(m):
            content = m["body"]
            sha = hashlib.sha256(content.encode()).hexdigest()
            path = inc / (sha + ".cginc")
            path.write_text(content)
            dependencies[str(path.relative_to(out))] = sha
            return (
                m["condition"]
                + '#include "Assets/NativeShaders/SemanticV2Variants/'
                + sha
                + '.cginc"\n'
            )

        body = branch.sub(pack, body)
        programs.append("CGPROGRAM" + body + "ENDCG")
        counts.append({"pass": index, "prior_sets": len(oldkeys), "recovered_sets": len(newkeys)})
    merged = before
    for m, replacement in reversed(list(zip(old, programs))):
        merged = merged[: m.start()] + replacement + merged[m.end() :]
    # Newly recovered full families may have unnamed/duplicate fallback passes.
    seen = set()
    edits = []
    delta = 0
    for si, (a, b, sub) in enumerate(list(blocks(merged, "SubShader"))):
        shift = 0
        for pi, (pa, pb, body) in enumerate(list(blocks(sub, "Pass"))):
            if "CGPROGRAM" not in body:
                continue
            header = body.split("CGPROGRAM")[0]
            nm = re.search(r'\bName\s+"([^"]*)"', header)
            mode = re.search(r'"LIGHTMODE"\s*=\s*"([^"]+)"', header)
            mode = mode[1].upper() if mode else "ALWAYS"
            name = nm[1] if nm else ""
            if not name or (mode, name) in seen:
                newname = (
                    name + "_NATIVE_SUB" + str(si) if name else f"NATIVE_RECOVERED_SUB{si}_PASS{pi}"
                )
                replacement = (
                    body[: nm.start()] + f'Name "{newname}"' + body[nm.end() :]
                    if nm
                    else body.replace("Pass {", f'Pass {{\nName "{newname}"', 1)
                )
                assert replacement != body
                edits.append({"subshader": si, "pass": pi, "before": name, "after": newname})
                name = newname
            else:
                replacement = body
            assert (mode, name) not in seen
            seen.add((mode, name))
            sub = sub[: pa + shift] + replacement + sub[pb + shift :]
            shift += len(replacement) - (pb - pa)
        merged = merged[: a + delta] + sub + merged[b + delta :]
        delta += len(sub) - (b - a)
    target = out / r["baseline_source"]
    assert not target.exists(), target
    target.write_text(merged)
    rows.append(
        {
            "identity": r,
            "status": "candidate_requires_native_validation",
            "source": str(target.relative_to(root)),
            "sha256": hashlib.sha256(merged.encode()).hexdigest(),
            "stereo_index_repairs": stereo,
            "pass_sets": counts,
            "pass_name_changes": edits,
            "raw_source": str(raw.relative_to(root)),
            "gather_repairs": gather_repairs,
        }
    )
(out / "manifest.json").write_text(
    json.dumps(
        {"rows": rows, "held": held, "dependencies": dependencies, "staged": False}, indent=2
    )
)
print(
    "Candidates",
    sum(r["status"].startswith("candidate") for r in rows),
    "preserved",
    sum(r["status"] == "preserve_current_source" for r in rows),
    "held",
    len(held),
    "includes",
    len(dependencies),
)
print([(r["identity"]["name"], r["markers"]) for r in held])
