using System.Reflection;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL.Metadders;

var check =
    typeof(USILCBufferMetadder)
        .GetMethod("IsIdentityComponentMask", BindingFlags.NonPublic | BindingFlags.Static) ??
    throw new InvalidOperationException("Missing CBuffer identity-mask rule");

var cases = new(string Name, int[] Mask, int Width, bool HideMask)[] {
    ("complete float2 identity", [0, 1], 2, true),
    ("repeated scalar broadcast .yy", [1, 1], 2, false),
    ("repeated scalar broadcast .xx", [0, 0], 2, false),
    ("float2 permutation .yx", [1, 0], 2, false),
    ("float4 permutation .wzyx", [3, 2, 1, 0], 4, false),
    ("partial lane .y", [1], 2, false),
    ("partial lanes .xz", [0, 2], 4, false),
    ("wrong width", [0, 1, 2], 2, false),
};

foreach (var item in cases) {
    bool actual = (bool)(check.Invoke(null, [item.Mask, item.Width]) ??
                         throw new InvalidOperationException("Identity-mask rule returned null"));
    if (actual != item.HideMask)
        throw new InvalidOperationException(
            $"{item.Name}: expected hide={item.HideMask}, got {actual}");
}

var arrayOperand = new USILOperand {
    operandType = USILOperandType.ConstantBuffer,
    metadataNameAssigned = true,
    metadataNameWithArray = true,
    metadataName = "LutParams",
    arrayIndex = 3,
    mask = [1, 1],
};
if (arrayOperand.ToString() != "LutParams[3].yy")
    throw new InvalidOperationException($"Array broadcast lost its swizzle: {arrayOperand}");

var offsetOperand = new USILOperand {
    operandType = USILOperandType.ConstantBuffer,
    metadataNameAssigned = true,
    metadataName = "LutParams",
    mask = [1, 0],
};
if (offsetOperand.ToString() != "LutParams.yx")
    throw new InvalidOperationException(
        $"Direct parameter permutation lost its swizzle: {offsetOperand}");

var offsetMap =
    typeof(USILCBufferMetadder)
        .GetMethod("MatchMaskToConstantBuffer", BindingFlags.NonPublic | BindingFlags.Static) ??
    throw new InvalidOperationException("Missing CBuffer offset mapper");
var mappedOffset =
    (int[])(offsetMap.Invoke(null, new object?[] { new[] { 2, 3 }, 8, 2 }) ??
            throw new InvalidOperationException("CBuffer offset mapper returned null"));
if (!mappedOffset.SequenceEqual([0, 1]))
    throw new InvalidOperationException(
        $"CBuffer register offset mapping changed: [{string.Join(',', mappedOffset)}]");

Console.WriteLine(
    $"Passed {cases.Length} CBuffer swizzle regressions plus array/offset mapping and formatting.");
