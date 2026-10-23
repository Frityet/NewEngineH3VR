from pathlib import Path

root = Path(__file__).resolve().parents[1] / ".tools/assetripper-source-1.0.15/Source"
base = Path("AssetRipper.Export.Modules.Shader/UltraShaderConverter")
p = root / base / "UShader/DirectX/DirectXProgramToUSIL.cs"
s = p.read_text()
a = s.index("private void HandleDp2")
b = s.index("private void HandleSample(", a)
s = (
    s[:a]
    + s[a:b]
    .replace("usilSrc0, mask, false", "usilSrc0, MapMask(mask, src0.swizzle), false")
    .replace("usilSrc1, mask, false", "usilSrc1, MapMask(mask, src1.swizzle), false")
    + s[b:]
)
p.write_text(s)
p = root / base / "UShader/Function/UShaderFunctionToHLSL.cs"
s = p.read_text()
a = s.index("private void HandleSample(")
b = s.index("private void HandleSampleLOD", a)
block = s[a:b]
block = block.replace(
    "string comment = CommentString(inst);",
    'if (inst.instructionType == USILInstructionType.SampleComparison || inst.instructionType == USILInstructionType.SampleComparisonLODZero)\n                value = $"UNITY_SAMPLE_SHADOW({srcOps[2]}, float3({srcOps[0]}, {srcOps[3]}))";\n            string comment = CommentString(inst);',
)
s = s[:a] + block + s[b:]
p.write_text(s)
p = root / "AssetRipper.Export.UnityProjects/Shaders/UnityShaderConstants.cs"
s = p.read_text().replace(
    '"unity_ObjectToWorld",',
    '"unity_DeltaTime", "unity_OrthoParams", "unity_CameraProjection", "unity_CameraInvProjection", "unity_WorldToCamera", "unity_CameraToWorld",\n            "unity_ObjectToWorld",',
)
p.write_text(s)
p = root / "AssetRipper.Export.UnityProjects/Shaders/USCShaderExporter.cs"
s = p.read_text()
s = s.replace(
    "using Stream fileStream = File.Create(path);",
    'using Stream fileStream = File.Create(path);\n            string? manifest = Environment.GetEnvironmentVariable("H3VR_SHADER_MANIFEST");\n            if (manifest != null) File.AppendAllText(manifest, $"{asset.Collection.Name}\\t{asset.PathID}\\t{path}\\n");',
)
s = s.replace(
    'writer.WriteLine($"sampler2D {name};");',
    'writer.WriteLine(name == "_VertexLight_ShadowMap" ? $"UNITY_DECLARE_SHADOWMAP({name});" : $"sampler2D {name};");',
)
p.write_text(s)
# Emit exact IEEE NaN bit pattern rather than illegal identifier in float literals.
p = root / base / "USIL/USILOperand.cs"
s = (
    p.read_text()
    .replace(
        'immValueFloat[i].ToString("0.0######", CultureInfo.InvariantCulture)',
        "FormatFloat(immValueFloat[i])",
    )
    .replace(
        'immValueFloat[0].ToString("0.0######", CultureInfo.InvariantCulture)',
        "FormatFloat(immValueFloat[0])",
    )
)
pos = (
    s.index("\n\t\tpublic override string ToString")
    if "\n\t\tpublic override string ToString" in s
    else -1
)
assert pos > 0
s = (
    s[:pos]
    + '\n        private static string FormatFloat(float value) => float.IsFinite(value) ? value.ToString("0.0######", CultureInfo.InvariantCulture) : $"asfloat(0x{BitConverter.SingleToUInt32Bits(value):X8}u)";\n'
    + s[pos:]
)
p.write_text(s)
