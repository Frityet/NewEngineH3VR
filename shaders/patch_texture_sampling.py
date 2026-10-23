"""Patch the pinned exporter; preserve original sample bindings, masks and offsets.

Run after the earlier NativeH3VR exporter patches. This intentionally uses texture
objects: adding a base-level UV delta is not equivalent to a texel offset at an
implicitly selected or fractional mip level.
"""

from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = root / ".tools/assetripper-source-1.0.15/Source"
base = source / "AssetRipper.Export.Modules.Shader/UltraShaderConverter"

path = base / "DirectXDisassembler/Blocks/SHDR.cs"
text = path.read_text(encoding="utf-8-sig")
for suffix, shift, mask in [("U", 9, "0x1e00"), ("V", 13, "0x1e000"), ("W", 17, "0x1e0000")]:
    old = f"addrOffsetI{suffix} = (extOpcodeToken & {mask}) >> {shift};"
    new = f"addrOffsetI{suffix} = ((extOpcodeToken >> {shift}) & 15) - (((extOpcodeToken >> {shift}) & 8) << 1);"
    assert old in text or new in text
    text = text.replace(old, new)
path.write_text(text)

path = base / "USIL/USILInstruction.cs"
text = path.read_text(encoding="utf-8-sig")
if "textureOffsets" not in text:
    text = text.replace(
        "public bool saturate { get; set; }",
        """public bool saturate { get; set; }
        // Original signed texel-space offsets; never fold these into UVs.
        public int[] textureOffsets { get; set; } = Array.Empty<int>();""",
    )
path.write_text(text)

path = base / "UShader/DirectX/DirectXProgramToUSIL.cs"
text = path.read_text(encoding="utf-8-sig")
start = text.index("private static int[] MapMask(")
end = text.index("private void HandleMov(", start)
text = (
    text[:start]
    + """private static int[] MapMask(int[] destMask, int[] srcMask)
        {
            if (srcMask == null || srcMask.Length == 0) return destMask;
            if (destMask == null || destMask.Length == 0) return srcMask;
            // A scalar source selector is broadcast; a four-lane source
            // swizzle is indexed by each destination lane, including .w.
            if (srcMask.Length == 1) return Enumerable.Repeat(srcMask[0], destMask.Length).ToArray();
            return destMask.Select(lane => srcMask[lane]).ToArray();
        }

        """
    + text[end:]
)
for method, following in [
    ("HandleSample", "HandleSampleC"),
    ("HandleSampleC", "HandleSampleL"),
    ("HandleSampleL", "HandleSampleD"),
    ("HandleSampleD", "HandleLd"),
]:
    start = text.index(f"private void {method}(")
    end = text.index(f"private void {following}(", start)
    part = text[start:end]
    if "textureOffsets =" not in part:
        part = part.replace(
            "destOperand = usilDest,",
            """destOperand = usilDest,
                saturate = inst.saturated,
                textureOffsets = SampleOffsets(inst, dimension),""",
        )
    if method == "HandleSampleD":
        part = part.replace(
            "FillUSILOperand(srcXDerivatives, usilSrcXDerivatives, srcXDerivatives.swizzle, false);",
            "FillUSILOperand(srcXDerivatives, usilSrcXDerivatives, MapMask(GetSampleGradientMask(dimension), srcXDerivatives.swizzle), false);",
        )
        part = part.replace(
            "FillUSILOperand(srcYDerivatives, usilSrcYDerivatives, srcYDerivatives.swizzle, false);",
            "FillUSILOperand(srcYDerivatives, usilSrcYDerivatives, MapMask(GetSampleGradientMask(dimension), srcYDerivatives.swizzle), false);",
        )
        part = part.replace(
            "MapMask(GetSampleOffsetMask(dimension), src",
            "MapMask(GetSampleGradientMask(dimension), src",
        )
    text = text[:start] + part + text[end:]
if "private static int[] SampleOffsets(" not in text:
    start = text.index("private static int[] GetSampleLocationMask(")
    text = (
        text[:start]
        + """private static int[] SampleOffsets(SHDRInstruction inst, ResourceDimension dimension)
        {
            int[] all = { inst.addrOffsetIU, inst.addrOffsetIV, inst.addrOffsetIW };
            if (all.All(v => v == 0)) return Array.Empty<int>();
            int count = dimension switch {
                ResourceDimension.texture1d or ResourceDimension.texture1darray => 1,
                ResourceDimension.texture2d or ResourceDimension.texture2darray => 2,
                ResourceDimension.texture3d => 3,
                _ => throw new NotSupportedException("Texel offsets on " + dimension)
            };
            if (all.Skip(count).Any(v => v != 0))
                throw new InvalidDataException("Nonzero offset outside texture dimensions");
            return all.Take(count).ToArray();
        }

        """
        + text[start:]
    )
path.write_text(text)

if "private static int[] GetSampleGradientMask(" not in text:
    start = text.index("private static int[] GetSampleLocationMask(")
    text = (
        text[:start]
        + """private static int[] GetSampleGradientMask(ResourceDimension dimension)
        {
            int count = dimension switch {
                ResourceDimension.texture1d or ResourceDimension.texture1darray => 1,
                ResourceDimension.texture2d or ResourceDimension.texture2darray => 2,
                ResourceDimension.texture3d or ResourceDimension.texturecube or ResourceDimension.texturecubearray => 3,
                _ => throw new NotSupportedException("Sample gradients on " + dimension)
            };
            return GetMaskOfSize(count);
        }

        """
        + text[start:]
    )
    path.write_text(text)

path = base / "UShader/Function/UShaderFunctionToHLSL.cs"
text = path.read_text(encoding="utf-8-sig")
start = (
    text.index("        private static string TextureOffset(")
    if "private static string TextureOffset(" in text
    else text.index("\t\tprivate void HandleSample(")
)
end = text.index("\t\tprivate void HandleLoadResource(", start)
replacement = """        private static string TextureOffset(USILInstruction inst)
        {
            var values = inst.textureOffsets;
            if (values.Length == 0) return "";
            if (values.Length > 3 || values.Any(v => v < -8 || v > 7))
                throw new InvalidDataException("Invalid original texture offset");
            return values.Length == 1 ? ", " + values[0] :
                ", int" + values.Length + "(" + string.Join(", ", values) + ")";
        }

        private static string TextureName(USILOperand operand)
        {
            if (!operand.metadataNameAssigned || string.IsNullOrEmpty(operand.metadataName))
                throw new InvalidDataException("Texture/sampler binding metadata is missing: " + operand);
            return operand.ToString(true);
        }

        private void EmitTextureResult(USILInstruction inst, string value, bool scalar = false)
        {
            // Resource swizzle is already composed with the destination write
            // mask by DirectXProgramToUSIL. It describes the returned channels,
            // not a swizzle to apply to the texture object or UV coordinates.
            var mask = inst.srcOperands[1].mask;
            if (mask.Length == 0 || mask.Length > 4 || mask.Any(v => v < 0 || v > 3))
                throw new InvalidDataException("Invalid texture result mask");
            if (scalar) {
                if (mask.Length > 1) value = "float" + mask.Length + "(" +
                    string.Join(", ", Enumerable.Repeat(value, mask.Length)) + ")";
            } else value += "." + new string(mask.Select(v => "xyzw"[v]).ToArray());
            AppendLine($"{CommentString(inst)}{inst.destOperand} = {WrapSaturate(inst, value)};");
        }

        private void HandleSample(USILInstruction inst)
        {
            var operands = inst.srcOperands;
            string texture = TextureName(operands[1]);
            string sampler = "sampler" + TextureName(operands[2]);
            bool comparison = inst.instructionType == USILInstructionType.SampleComparison ||
                inst.instructionType == USILInstructionType.SampleComparisonLODZero;
            string method = comparison ? (inst.instructionType == USILInstructionType.SampleComparisonLODZero ?
                "SampleCmpLevelZero" : "SampleCmp") : "Sample";
            string reference = comparison ? ", " + operands[3] : "";
            EmitTextureResult(inst, $"{texture}.{method}({sampler}, {operands[0]}{reference}{TextureOffset(inst)})", comparison);
        }

        private void HandleSampleLOD(USILInstruction inst)
        {
            var operands = inst.srcOperands;
            string texture = TextureName(operands[1]);
            string sampler = "sampler" + TextureName(operands[2]);
            string method = inst.instructionType == USILInstructionType.SampleLODBias ? "SampleBias" : "SampleLevel";
            EmitTextureResult(inst, $"{texture}.{method}({sampler}, {operands[0]}, {operands[3]}{TextureOffset(inst)})");
        }

        private void HandleSampleDerivative(USILInstruction inst)
        {
            var operands = inst.srcOperands;
            string texture = TextureName(operands[1]);
            string sampler = "sampler" + TextureName(operands[2]);
            EmitTextureResult(inst, $"{texture}.SampleGrad({sampler}, {operands[0]}, {operands[3]}, {operands[4]}{TextureOffset(inst)})");
        }

"""
text = text[:start] + replacement + text[end:]
path.write_text(text)

# Declarations use original texture dimensions and sampler modes. UnityCG
# declares _ShadowMapTexture only for legacy SHADOW_COLLECTOR_PASS helpers.
path = source / "AssetRipper.Export.UnityProjects/Shaders/USCShaderExporter.cs"
text = path.read_text(encoding="utf-8-sig")
for stage in ("vertex", "fragment"):
    text = text.replace(
        f"ExportPassTextureParamDefinitions({stage}SubProgram!, writer,",
        f"ExportPassTextureParamDefinitions({stage}SubProgram!, {stage}Converter!.DxShader!, writer,",
    )
start = (
    text.index("\t\tprivate static string GetTextureDeclaration")
    if "private static string GetTextureDeclaration" in text
    else text.index("\t\tprivate static void ExportPassTextureParamDefinitions")
)
end = text.index("\t\tprivate static void ExportSerializedProgramDecomp", start)
text = (
    text[:start]
    + (root / "shaders/texture-declaration-template.cs.txt").read_text()
    + "\n"
    + text[end:]
)
path.write_text(text)
print("Patched exact texture sampling and metadata-based texture/sampler declarations.")
