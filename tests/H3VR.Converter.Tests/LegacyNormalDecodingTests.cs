using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyNormalDecodingTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("recovered normal decode reads Unity RG or AG without changing other channel uses", RewriteNormal),
        ("recovered normal decode keeps the tangent normal for detail, terrain and layered materials", RewriteLayeredNormals),
        ("recovered normal decode requires the sampled register and leaves alpha masks alone", KeepUnmatchedSource),
        ("recovered DXT5 normal coordinates survive transparent-colour dilation", PreserveNormalCoordinates),
        ("uncompressed original colour textures keep their default platform encoding", PreserveUncompressedColors),
    ];

    private static void PreserveNormalCoordinates()
    {
        const string source = "TextureImporter:\n  textureFormat: 12\n  alphaIsTransparency: 1\n  textureType: 1\n  textureShape: 1\n";
        const string flag = "alphaIsTransparency: 1";
        foreach (var format in new[] { "12", "29" })
        {
            var input = source.Replace("textureFormat: 12", $"textureFormat: {format}");
            var expected = input.Replace(flag, "alphaIsTransparency: 0");
            Check(LegacyTextureImport.PreserveStockFormat(input), expected);
            Check(LegacyTextureImport.PreserveStockFormat(expected), expected);
            Check(LegacyTextureImport.PreserveStockFormat(input.ReplaceLineEndings("\r\n")), expected.ReplaceLineEndings("\r\n"));
        }
        foreach (var other in new[] { source.Replace("textureType: 1", "textureType: 0"),
            source.Replace("textureFormat: 12", "textureFormat: 4"), source.Replace("textureShape: 1", "textureShape: 2") })
            Check(LegacyTextureImport.PreserveStockFormat(other), other);
    }

    private static void PreserveUncompressedColors()
    {
        const string source = """
            TextureImporter:
              textureFormat: 3
              alphaIsTransparency: 1
              textureType: 0
              textureShape: 1
              platformSettings:
              - buildTarget: DefaultTexturePlatform
                maxTextureSize: 128
                textureFormat: -1
                textureCompression: 2
              - buildTarget: Standalone
                textureFormat: 4
                textureCompression: 1
            """;
        foreach (var format in new[] { "3", "4", "5" })
        {
            var original = source.Replace("textureFormat: 3", $"textureFormat: {format}");
            var repaired = original.Replace("textureCompression: 2", "textureCompression: 0");
            Check(LegacyTextureImport.PreserveStockFormat(original), repaired);
            Check(LegacyTextureImport.PreserveStockFormat(repaired), repaired);
        }
        foreach (var other in new[] { source.Replace("textureFormat: 3", "textureFormat: 12"),
            source.Replace("textureFormat: -1", "textureFormat: 25"), source.Replace("textureType: 0", "textureType: 1") })
            Check(LegacyTextureImport.PreserveStockFormat(other), other);
    }

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

    private static void RewriteLayeredNormals()
    {
        const string source = """
            tmp5 = _DetailNormalMap.Sample(sampler_DetailNormalMap, tmp4.yz).xyzw;
            tmp1.x = tmp1.x * _DetailNormalMapScale;
            tmp4.yz = tmp5.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp9.y = tmp5.w;
            tmp6 = _Normal.Sample(sampler_Normal, tmp3.xy).xyzw;
            tmp6.xy = tmp6.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp7 = _Layer1Normal.Sample(sampler_Layer1Normal, tmp3.zw).xyzw;
            tmp7.xy = tmp7.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp8 = _Normal3.Sample(sampler_Normal3, tmp2.yz).xyzw;
            tmp3.xz = tmp8.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp1 = _BumpMap2.Sample(sampler_BumpMap2, tmp0.xy).xyzw;
            tmp0 = _MainTex2.Sample(sampler_MainTex2, tmp0.xy).xyzw;
            tmp1.xy = tmp1.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp16 = _BumpMap3.SampleLevel(sampler_BumpMap3, tmp4.xy, tmp7.y).xyzw;
            tmp3.yw = tmp16.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            """;
        var expected = source;
        foreach (int index in new[] { 5, 6, 7, 8, 1, 16 })
        {
            var register = $"tmp{index}";
            expected = expected.Replace($"{register}.wy", $"float2({register}.w * {register}.x, {register}.y)",
                StringComparison.Ordinal);
        }
        Check(LegacyNormalDecoding.Apply(source), expected);
        Check(LegacyNormalDecoding.Apply(expected), expected);
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
            tmp5 = _DetailNormalMap.Sample(sampler_DetailNormalMap, tmp4.yz).xyzw;
            tmp5.x = tmp5.x * _DetailNormalMapScale;
            tmp4.yz = tmp5.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            tmp2 = _BentNormalMap.Sample(sampler_BentNormalMap, tmp1.xy).xyzw;
            tmp3.xy = tmp2.wy * float2(2.0, 2.0) + float2(-1.0, -1.0);
            """;
        Check(LegacyNormalDecoding.Apply(source), source);
    }

    private static void Check(string actual, string expected)
    {
        if (actual != expected) throw new Exception("Unexpected recovered normal decode edit.");
    }
}
