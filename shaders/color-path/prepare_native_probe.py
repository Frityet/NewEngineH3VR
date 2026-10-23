"""Create an isolated native Metal probe from the freshly emitted Uber include variants."""

from pathlib import Path
import hashlib, json, re, shutil

root = Path(__file__).resolve().parents[2]
base = root / "shaders/color-path/semantic-v3-packaged"
project = root / "color-path-probe-swizzle"
assert not project.exists(), project
shader = (base / "Hidden_PostProcessing_Uber.shader").read_text()
include_root = base / "Assets/NativeShaders/SemanticV2Variants"
required = {"COLOR_GRADING_HDR_3D", "FINALPASS"}
selected = []
for want_bloom in (False, True):
    rows = []
    for m in re.finditer(
        r'^\s*#(?:if|elif) (.+)\n\s*#include "(Assets/NativeShaders/SemanticV2Variants/[^\"]+)"',
        shader,
        re.M,
    ):
        cond, path = m.groups()
        positive = set(re.findall(r"(?<!\!)defined\((\w+)\)", cond))
        negative = set(re.findall(r"!defined\((\w+)\)", cond))
        if not required <= positive or {"COLOR_GRADING_HDR_2D", "COLOR_GRADING_LDR_2D"} & positive:
            continue
        if want_bloom and "BLOOM" not in positive:
            continue
        if not want_bloom and "BLOOM" not in negative:
            continue
        score = len(positive - (required | ({"BLOOM"} if want_bloom else set())))
        rows.append((score, cond, path))
    rows.sort(key=lambda x: (x[0], x[2]))
    bodies = []
    for score, cond, path in rows:
        if score:
            continue
        body = (include_root / Path(path).name).read_text()
        sha = hashlib.sha256(body.encode()).hexdigest()
        if all(sha != x["sha256"] for x in bodies):
            bodies.append({"condition": cond, "path": path, "body": body, "sha256": sha})
        if len(bodies) == 2:
            break
    assert len(bodies) == 2, (want_bloom, len(bodies))
    selected.extend(bodies)
shutil.copytree(
    root / "color-path-probe",
    project,
    ignore=shutil.ignore_patterns("Library", "Temp", "obj", "Logs", "output"),
)
(project / "output").mkdir(parents=True, exist_ok=True)
# The machine's installed Unity editor is 2020.3; write a compact report without JsonUtility.
cs = project / "Assets/ColorProbe.cs"
code = cs.read_text()
old = 'File.WriteAllText(Environment.GetEnvironmentVariable("COLOR_PROOF_REPORT"),JsonUtility.ToJson(report,true));'
new = 'File.WriteAllText(Environment.GetEnvironmentVariable("COLOR_PROOF_REPORT"),"{\\"passed\\":"+report.passed.ToString().ToLower()+",\\"api\\":\\""+report.api+"\\",\\"device\\":\\""+report.device+"\\",\\"positives\\":"+positives+",\\"negativeDetected\\":"+negative+",\\"swizzleOnlyDetected\\":"+swizzleOnly+",\\"maxError\\":"+max+",\\"errors\\":"+errors.Count+"}");'
assert old in code
cs.write_text(code.replace(old, new))
proof = []
for index, item in enumerate(selected):
    body = re.sub(r"\blog\(", "log2(", re.sub(r"\bexp\(", "exp2(", item["body"]))
    assert (
        "_Lut3D_Params.yy" in body and "_Lut3D_Params.xx" in body and "* _Lut3D_Params;" not in body
    )
    wrapper = f"""Shader "ColorPath/EmitterCandidate{index}" {{ SubShader {{ Cull Off ZWrite Off ZTest Always Pass {{ CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n#pragma target 4.5\nfloat4 unity_StereoScaleOffset[2];\n{body}\nENDCG }} }} }}\n"""
    (project / f"Assets/Resources/Corrected{index}.shader").write_text(wrapper)
    proof.append(
        {
            "index": index,
            "source_include": item["path"],
            "condition": item["condition"],
            "source_sha256": item["sha256"],
            "base_two_intrinsic_mapping": "DXBC LOG/EXP -> log2/exp2 in the test harness",
        }
    )
(root / "shaders/color-path/emitter-native-probe-sources.json").write_text(
    json.dumps(proof, indent=2)
)
print(
    "Prepared",
    project,
    "with",
    len(proof),
    "freshly emitted corrected shader variants; original negative controls copied unchanged.",
)
