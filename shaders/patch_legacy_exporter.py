from pathlib import Path

root = Path(__file__).resolve().parents[1] / ".tools/assetripper-source-1.0.15/Source"


def edit(p, old, new):
    p = root / p
    s = p.read_text()
    assert old in s, (p, old)
    p.write_text(s.replace(old, new))


edit(
    Path("AssetRipper.SourceGenerated.Extensions/VertexDataExtensions.cs"),
    "buff = buff.Reverse().ToArray();",
    "Array.Reverse(buff);",
)
base = Path("AssetRipper.Export.Modules.Shader/UltraShaderConverter")
edit(
    base / "USIL/USILOptimizerApplier.cs",
    "new USILForLoopOptimizer(),",
    "// Disabled: renames loop registers without reliably creating for-loop declarations.\n\t\t\t// new USILForLoopOptimizer(),",
)
edit(
    base / "UShader/Function/UShaderFunctionToHLSL.cs",
    "{ USILInstructionType.SampleLOD, new InstHandler(HandleSampleLOD) },",
    "{ USILInstructionType.SampleLOD, new InstHandler(HandleSampleLOD) },\n                { USILInstructionType.SampleLODBias, new InstHandler(HandleSampleLOD) },",
)
edit(
    base / "UShader/Function/UShaderFunctionToHLSL.cs",
    'string comment = CommentString(inst);\n\t\t\tAppendLine($"{comment}{inst.destOperand} = {value};");\n\t\t}\n\n\t\tprivate void HandleSampleDerivative',
    'if (inst.instructionType == USILInstructionType.SampleLODBias) value = value.Replace("lod(", "bias(");\n\t\t\tstring comment = CommentString(inst);\n\t\t\tAppendLine($"{comment}{inst.destOperand} = {value};");\n\t\t}\n\n\t\tprivate void HandleSampleDerivative',
)
p = root / base / "USIL/Metadders/USILCBufferMetadder.cs"
s = p.read_text()
s = s.replace(
    "int paramCbSize = param.RowCount * param.ColumnCount * 4;",
    "int paramStride = param.IsMatrix ? param.RowCount * param.ColumnCount * 4 : 16;\n                    int paramCbSize = param.ArraySize > 0 ? paramStride * param.ArraySize : param.RowCount * param.ColumnCount * 4;",
).replace(
    "int maskIndex = (operandMaskAddress - paramCbStart) / 4;",
    "int maskIndex = (operandMaskAddress - paramCbStart) / 4;\n                            if (param.ArraySize > 0) maskIndex %= paramStride / 4;",
)
old = "operand.metadataNameWithArray = operand.arrayRelative != null && !param.IsMatrix;"
new = """operand.metadataNameWithArray = false;
                    if (param.ArraySize > 0) {
                        int registersPerElement = param.IsMatrix ? param.RowCount : 1;
                        string relative = operand.arrayRelative == null ? "0" : $"(int)({operand.arrayRelative})";
                        operand.metadataName = $"{param.Name}[({relative} + {operand.arrayIndex}) / {registersPerElement}]";
                        operand.arrayIndex %= registersPerElement;
                        operand.arrayRelative = null;
                    }
"""
assert old in s
s = s.replace(old, new)
p.write_text(s)
