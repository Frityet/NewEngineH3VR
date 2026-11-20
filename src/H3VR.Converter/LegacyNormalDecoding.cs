using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static partial class LegacyNormalDecoding
{
    internal const string MarkerPath = "Assets/Resources/NativeH3VR/LegacyNormalDecoding.txt";

    // Match only the recovery tool's normal decode expression and its sample.
    // Some variants interleave an albedo sample or scale the detail weight.
    [GeneratedRegex("""
        (?<sample>
            ^[ \t]*(?<texel>tmp\d+) [ ]=[ ]
            (?<map>_BumpMap[23]?|_DetailNormalMap|_Normal[0-3]?|_Layer1Normal)
            \.Sample(?:Level)?\(sampler\k<map>,[^\r\n;]*\)\.xyzw;\r?\n
            (?:
                [ \t]*(?!\k<texel>\b)tmp\d+
                (?:
                    [ ]=[ ](?<albedo>_MainTex[2-4]?)\.Sample\(sampler\k<albedo>,[^\r\n;]*\)\.xyzw
                    | \.[xyzw][ ]=[ ](?:tmp\d+\.[xyzw][ ]\*[ ]_DetailNormalMapScale
                                      |_DetailNormalMapScale[ ]\*[ ]_DetailWeight)
                );\r?\n
            )?
            [ \t]*tmp\d+\.[xyzw]{2}[ ]=[ ]
        )
        \k<texel>\.wy(?=[ ]\*[ ]float2\(2\.0,[ ]2\.0\)[ ]\+[ ]float2\(-1\.0,[ ]-1\.0\);)
        """, RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LegacyDecode();

    internal static string Apply(string source) =>
        // Unity's RG-or-AG decoder multiplies red and alpha. In either packing,
        // the unused channel is one. The branch's other reads keep the source texel.
        LegacyDecode().Replace(source, "${sample}float2(${texel}.w * ${texel}.x, ${texel}.y)");
}
