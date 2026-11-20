using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyNormalDecodingTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("recovered normal decode reads Unity RG or AG without changing other channel uses", RewriteNormal),
        ("recovered normal decode requires the sampled register and leaves alpha masks alone", KeepUnmatchedSource),
    ];

    private static void RewriteNormal()
    {
        const string source = """
            tmp13 = _BumpMap.Sample(sampler_BumpMap, tmp4.xy).xyzw;
            tmp7.zw = tmp13.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp4.w = tmp13.w;
            tmp5 = _BumpMap2.Sample(sampler_BumpMap2, tmp1.xy).xyzw;
            tmp8 = _MainTex.Sample(sampler_MainTex, tmp1.xy).xyzw;
            tmp2.yz = tmp5.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            """;
        var expected = source
            .Replace("tmp13.wy", "float2(tmp13.w * tmp13.x, tmp13.y)", StringComparison.Ordinal)
            .Replace("tmp5.wy", "float2(tmp5.w * tmp5.x, tmp5.y)", StringComparison.Ordinal);
        Check(LegacyNormalDecoding.Apply(source), expected);
        Check(LegacyNormalDecoding.Apply(expected), expected);
        Check(LegacyNormalDecoding.Apply(source.ReplaceLineEndings("\r\n")), expected.ReplaceLineEndings("\r\n"));
    }

    private static void KeepUnmatchedSource()
    {
        const string source = """
            tmp2 = _BumpMap.Sample(sampler_BumpMap, tmp1.xy).xyzw;
            tmp2 = _MainTex.Sample(sampler_MainTex, tmp1.xy).xyzw;
            tmp3.xy = tmp2.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp5 = _BumpMap.Sample(sampler_BumpMap, tmp1.xy).xyzw;
            tmp3.xy = tmp7.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp2 = _Ao2Map.Sample(sampler_Ao2Map, tmp1.xy).xyzw;
            tmp3.xy = tmp2.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            """;
        Check(LegacyNormalDecoding.Apply(source), source);
    }

    private static void Check(string actual, string expected)
    {
        if (actual != expected) throw new Exception("Unexpected recovered normal decode edit.");
    }
}
