using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Security.Cryptography;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.Function;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.DirectX;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.DirectXDisassembler;

string root = Path.GetFullPath(args[0]);
void Require(bool condition, string detail) {
    if (!condition)
        throw new InvalidDataException(detail);
}
string Emit(USILInstruction instruction) {
    var program = new UShaderProgram();
    program.instructions.Add(instruction);
    return new UShaderFunctionToHLSL(program).Convert(0).Trim();
}
USILOperand Named(string name, USILOperandType type, string mask, bool display = false) => new() {
    metadataName = name, metadataNameAssigned = true, operandType = type,
    mask = mask.Select(c => "xyzw".IndexOf(c)).ToArray(), displayMask = display
};
var cases = new List<object>();
void Case(string id, USILInstructionType kind, string destination, string returned, int[] offset,
          int mip) {
    var operands =
        new List<USILOperand> { Named("uv", USILOperandType.InputRegister, "xy"),
                                Named("Source", USILOperandType.ResourceRegister, returned),
                                Named("Other", USILOperandType.Sampler2D, "xyzw") };
    string method;
    switch (kind) {
    case USILInstructionType.Sample:
        method = "Sample";
        operands.Add(new USILOperand(0));
        break;
    case USILInstructionType.SampleLOD:
        method = "SampleLevel";
        operands.Add(new USILOperand((float)mip));
        operands.Add(new USILOperand(0));
        break;
    case USILInstructionType.SampleLODBias:
        method = "SampleBias";
        operands.Add(new USILOperand(1.0f));
        operands.Add(new USILOperand(0));
        break;
    case USILInstructionType.SampleDerivative:
        method = "SampleGrad";
        operands.Add(Named("ddxUV", USILOperandType.InputRegister, "xy"));
        operands.Add(Named("ddyUV", USILOperandType.InputRegister, "xy"));
        break;
    default:
        throw new ArgumentOutOfRangeException();
    }
    var instruction =
        new USILInstruction { instructionType = kind,
                              destOperand = Named("result", USILOperandType.OutputRegister,
                                                  destination, true),
                              srcOperands = operands, textureOffsets = offset };
    string statement = Emit(instruction);
    string destinationText = destination == "xyzw" ? "result" : "result." + destination;
    Require(statement.StartsWith(destinationText + " = Source." + method + "(samplerOther, "),
            id + " binding/method: " + statement);
    Require(statement.EndsWith(")." + returned + ";"), id + " result channels");
    if (offset.Length > 0)
        Require(statement.Contains("int2(" + string.Join(", ", offset) + ")"),
                id + " texel offset");
    string expression = statement.Split(" = ", 2) [1].TrimEnd(';');
    cases.Add(new { id, statement, expression, destinationMask = destination, kind = method,
                    expected = new { resource = "Source", sampler = "Other", mip,
                                     offset = offset.Length == 0 ? new[] { 0, 0 } : offset,
                                     swizzle = returned } });
}
Case("sample-rgba", USILInstructionType.Sample, "xyzw", "xyzw", [], 1);
Case("sample-rotated", USILInstructionType.Sample, "xyzw", "wxyz", [], 1);
Case("sample-partial", USILInstructionType.Sample, "yw", "zx", [], 1);
Case("sample-offset", USILInstructionType.Sample, "xyzw", "zxyw", [-1, 1], 1);
Case("lod1-swizzle", USILInstructionType.SampleLOD, "xyzw", "yzxw", [], 1);
Case("lod1-offset", USILInstructionType.SampleLOD, "xyzw", "wzyx", [-1, 1], 1);
Case("lod2-offset", USILInstructionType.SampleLOD, "xz", "yw", [1, -1], 2);
Case("lod2-extreme-offset", USILInstructionType.SampleLOD, "xyzw", "zwxy", [-8, 7], 2);
Case("gradient-offset", USILInstructionType.SampleDerivative, "xyzw", "wyxz", [-1, 1], 2);
Case("gradient-partial", USILInstructionType.SampleDerivative, "w", "y", [1, -1], 2);
Case("bias-offset", USILInstructionType.SampleLODBias, "xyzw", "xwzy", [-1, 1], 2);
Case("bias-partial", USILInstructionType.SampleLODBias, "yz", "wx", [1, -1], 2);
File.WriteAllText(Path.Combine(root, "shaders/texture-sampling-tests/cases.json"),
                  JsonSerializer.Serialize(new { cases },
                                           new JsonSerializerOptions { WriteIndented = true }));

// Independently disassembled original programs supply resource channel order
// and signed offsets. Check their decoded USIL before metadata optimizers.
int originalPrograms = 0, originalSamples = 0, originalNonzeroOffsets = 0,
    originalNontrivialMasks = 0;
var sampleTypes = new HashSet<USILInstructionType> {
    USILInstructionType.Sample,           USILInstructionType.SampleLOD,
    USILInstructionType.SampleLODBias,    USILInstructionType.SampleDerivative,
    USILInstructionType.SampleComparison, USILInstructionType.SampleComparisonLODZero
};
foreach (string path in Directory
             .GetFiles(Path.Combine(root, "shaders/critical-bytecode-proof"), "*.dxbc")
             .Order()) {
    var asm = File.ReadAllLines(Path.ChangeExtension(path, ".asm"));
    var expected = asm.Select(l => l.Trim())
                       .Where(l => Regex.IsMatch(l, @"^sample(?:_l|_b|_d|_c(?:_lz)?)?(?:_|\s)"))
                       .ToArray();
    using var stream = File.OpenRead(path);
    var converter = new DirectXProgramToUSIL(new DirectXCompiledShader(stream));
    converter.Convert();
    var actual =
        converter.shader.instructions.Where(i => sampleTypes.Contains(i.instructionType)).ToArray();
    Require(actual.Length == expected.Length, path + " original sample count");
    for (int i = 0; i < actual.Length; i++) {
        string original = expected[i];
        var instruction = actual[i];
        var operands = original[(original.IndexOf(' ') + 1)..].Split(", ");
        string destMask = operands[0].Split('.')[1], resourceSwizzle = operands[2].Split('.')[1];
        string channels =
            new string(destMask.Select(c => resourceSwizzle["xyzw".IndexOf(c)]).ToArray());
        string actualChannels =
            new string(instruction.srcOperands[1].mask.Select(v => "xyzw"[v]).ToArray());
        Require(actualChannels == channels, path + " channel mapping expected=" + channels +
                                                " actual=" + actualChannels + ": " + original);
        var offset = Regex.Match(original, @"aoffimmi(?:_indexable)?\((-?\d+),(-?\d+),(-?\d+)\)");
        int[] expectedOffsets = offset.Success ? new[] { int.Parse(offset.Groups[1].Value),
                                                         int.Parse(offset.Groups[2].Value) }
                                               : [];
        Require(instruction.textureOffsets.SequenceEqual(expectedOffsets),
                path + " signed offsets: " + original);
        // Exercise the actual emitter with explicit independent bindings.
        instruction.srcOperands[1].metadataName = "OriginalResource";
        instruction.srcOperands[1].metadataNameAssigned = true;
        instruction.srcOperands[2].metadataName = "OriginalSamplerOwner";
        instruction.srcOperands[2].metadataNameAssigned = true;
        string emitted = Emit(instruction);
        Require(emitted.Contains("OriginalResource.Sample") &&
                    emitted.Contains("samplerOriginalSamplerOwner"),
                path + " original bindings");
        bool scalar =
            instruction.instructionType is
                USILInstructionType.SampleComparison or USILInstructionType.SampleComparisonLODZero;
        if (!scalar)
            Require(emitted.EndsWith("." + channels + ";"),
                    path + " emitted original result channels");
        originalSamples++;
        if (expectedOffsets.Length > 0)
            originalNonzeroOffsets++;
        if (channels != "xyzw"[..channels.Length])
            originalNontrivialMasks++;
    }
    originalPrograms++;
}
// Exhaust all ordinary destination masks and four-channel source swizzles,
// plus scalar source broadcasts; these cover the shared converter operation.
var mapMask = typeof(DirectXProgramToUSIL)
                  .GetMethod("MapMask", BindingFlags.NonPublic | BindingFlags.Static)!;
int maskCases = 0;
for (int bits = 1; bits < 16; bits++) {
    int[] destination = Enumerable.Range(0, 4).Where(i => (bits & (1 << i)) != 0).ToArray();
    for (int packed = 0; packed < 260; packed++) {
        int[] source = packed < 256
                           ? Enumerable.Range(0, 4).Select(i => (packed >> (2 * i)) & 3).ToArray()
                           : new[] { packed - 256 };
        int[] expected =
            destination.Select(lane => source.Length == 1 ? source[0] : source[lane]).ToArray();
        int[] actual = (int[])mapMask.Invoke(null, new object[] { destination, source })!;
        Require(actual.SequenceEqual(expected), "source/destination swizzle composition");
        maskCases++;
    }
}
string dll = typeof(USILInstruction).Assembly.Location;
var report =
    new { passed = true,
          syntheticCases = cases.Count,
          maskCases,
          originalPrograms,
          originalSamples,
          originalNonzeroOffsets,
          originalNontrivialMasks,
          exporterDllSha256 =
              Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(),
          limits =
              new[] { "Texture binding, result-mask and signed-offset regression only; unrelated " +
                      "arithmetic and original gather operations need separate proof." } };
File.WriteAllText(Path.Combine(root, "shaders/texture-sampling-tests/verification.json"),
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
