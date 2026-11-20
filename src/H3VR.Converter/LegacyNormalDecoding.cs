using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static partial class LegacyNormalDecoding
{
    internal const string MarkerPath = "Assets/Resources/NativeH3VR/LegacyNormalDecoding.txt";

    // The recovered program normally decodes immediately after the sample. Some
    // variants interleave one albedo sample, whose destination must be different.
    [GeneratedRegex("""
        (?m)(?<sample>^[ \t]*(?<texel>tmp\d+) = (?<map>_BumpMap2?)\.Sample\(sampler\k<map>,[^\r\n;]*\)\.xyzw;\r?\n(?:[ \t]*(?!\k<texel> = )tmp\d+ = _MainTex\.Sample\(sampler_MainTex,[^\r\n;]*\)\.xyzw;\r?\n)?[ \t]*tmp\d+\.(?:xy|yz|zw) = )\k<texel>\.wy(?= \* float2\(2\.0, 2\.0\) \+ float2\(-1\.0, -1\.0\);)
        """, RegexOptions.CultureInvariant)]
    private static partial Regex LegacyDecode();

    internal static string Apply(string source) =>
        // Unity's RG-or-AG decoder multiplies red and alpha. In either packing,
        // the unused channel is one. The branch's other reads keep the source texel.
        LegacyDecode().Replace(source, "${sample}float2(${texel}.w * ${texel}.x, ${texel}.y)");
}
