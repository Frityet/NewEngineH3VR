using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyPackedTextureDataTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("packed mask companions preserve every BC3 mip block", MipExtents),
        ("packed mask companions use exact linear BC3 data and stock sampling", NativeAsset),
        ("only exact and numbered recovered copies match the stock texture identity", ExportedCopies),
        ("stock additive sign textures keep sRGB sampling and complete BC3 mips", SignTexture),
        ("stock additive sign migration rebinds only its exact main texture", SignBinding),
        ("stock plinth warning migration requires premultiplied material state", WarningBinding),
    ];

    private static void MipExtents()
    {
        foreach (var (width, height, mips, bytes) in new[]
        {
            (8, 8, 4, 112), (8, 2, 4, 80), (1024, 1024, 11, 1_398_128), (2048, 2048, 12, 5_592_432),
        })
        {
            if (LegacyPackedTextureData.MipBytes(width, height, mips) != bytes)
                throw new Exception("Unexpected BC3 mip extent.");
            try { LegacyPackedTextureData.MipBytes(width, height, mips - 1); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Accepted a truncated BC3 mip chain.");
        }
    }

    private static void NativeAsset()
    {
        byte[] contents = Enumerable.Range(0, 112).Select(i => (byte)i).ToArray();
        var texture = new LegacyPackedTextureData.PackedTexture(8, 8, 4, 2, 4, -0.25f, 1, contents);
        string yaml = LegacyPackedTextureData.TextureYaml("Fixture", texture);
        foreach (string required in new[]
        {
            "--- !u!28 &2800000", "m_TextureFormat: 12", "m_MipCount: 4", "m_LightmapFormat: 0",
            "m_ColorSpace: 0", "m_MipBias: -0.25", "m_WrapW: 1", "image data: 112",
            "_typelessdata: " + Convert.ToHexStringLower(contents),
        })
        {
            if (!yaml.Contains(required, StringComparison.Ordinal)) throw new Exception($"Missing {required}.");
        }
    }

    private static void SignTexture()
    {
        const int mips = 11;
        if (LegacyPackedTextureData.MipBytes(1024, 256, mips) != 349_584)
            throw new Exception("Unexpected stock sign mip extent.");
        var texture = new LegacyPackedTextureData.PackedTexture(8, 8, 4, 1, 1, 0, 0, new byte[112],
            ColorSpace: 1, IgnoreMipmapLimit: false);
        string yaml = LegacyPackedTextureData.TextureYaml("FixtureSign", texture);
        foreach (string field in new[] { "m_ColorSpace: 1", "m_IgnoreMipmapLimit: 0", "m_TextureFormat: 12", "m_MipCount: 4" })
            if (!yaml.Contains(field, StringComparison.Ordinal)) throw new Exception("Missing " + field);
    }

    private static void SignBinding()
    {
        const string stock = "25fd9307a0847d644aa971888b915ff0";
        const string derived = "63a14f95b23c4a0e0b8ad8b62049063ae";
        const string shader = "a93930d1adfa66c45b9aa2fd5bf93c02";
        const string freshShader = "d12b9bfbcba0c2948b8865b31e7d9a32";
        const string freshStock = "279548c8ba733bf4d847d5817222ae7c";
        string source = $$"""
            Material:
              m_Name: TAH_TouchToBegin
              m_Shader: {fileID: 4800000, guid: {{shader}}, type: 3}
              m_SavedProperties:
                m_TexEnvs:
                  _MainTex:
                    m_Texture: {fileID: 2800000, guid: {{stock}}, type: 3}
                    m_Scale: {x: 4, y: 1}
                    m_Offset: {x: 0, y: 0}
            """.ReplaceLineEndings("\r\n");
        string expected = source.Replace(stock + ", type: 3", derived + ", type: 2", StringComparison.Ordinal);
        if (LegacySignTextureMips.RewriteBinding(source, "TAH_TouchToBegin", stock, derived, new HashSet<string> { shader }) != expected)
            throw new Exception("Rebinding changed more than the original main texture reference.");
        string fresh = source.Replace(shader, freshShader).Replace(stock, freshStock);
        if (LegacySignTextureMips.RewriteBinding(fresh, "TAH_TouchToBegin", freshStock, derived,
                new HashSet<string> { freshShader }) != expected.Replace(shader, freshShader))
            throw new Exception("Rebinding depends on the prior extraction GUIDs.");
        foreach (string unrelated in new[] { expected, source.Replace(shader, stock), source.Replace("_MainTex", "_SpecTex") })
            if (LegacySignTextureMips.RewriteBinding(unrelated, "TAH_TouchToBegin", stock, derived, new HashSet<string> { shader }) is not null)
                throw new Exception("The sign migration accepted an unrelated binding.");
    }

    private static void WarningBinding()
    {
        const string shader = "4c0e43de6cb357741af885c85c8ae8e5";
        const string stock = "11111111111111111111111111111111";
        const string derived = "98811fc73d3ed644ce25032f2da236646";
        const string name = "MG_4Plinth_Warning1";
        var shaders = new HashSet<string> { shader };
        string source = $$"""
            Material:
              m_Name: {{name}}
              m_Shader: {fileID: 4800000, guid: {{shader}}, type: 3}
              m_ShaderKeywords: _ALPHAPREMULTIPLY_ON
              m_SavedProperties:
                m_TexEnvs:
                  _MainTex:
                    m_Texture: {fileID: 2800000, guid: {{stock}}, type: 3}
                m_Floats:
                  _DstBlend: 10
                  _SrcBlend: 1
            """;
        if (LegacySignTextureMips.RewriteBinding(source, name, stock, derived, shaders, premultiplied: true) is null)
            throw new Exception("Premultiplied warning was not bound.");
        foreach (string other in new[] { source.Replace("_ALPHAPREMULTIPLY_ON", "_ALPHABLEND_ON"), source.Replace("_DstBlend: 10", "_DstBlend: 0") })
            if (LegacySignTextureMips.RewriteBinding(other, name, stock, derived, shaders, premultiplied: true) is not null)
                throw new Exception("Changed warning blend mode was accepted.");
    }

    private static void ExportedCopies()
    {
        foreach (var (name, match) in new[] { ("Original", true), ("Original_2", true),
            ("Original_Copy", false), ("FooOriginal_1", false) })
        {
            if (LegacyPackedTextureData.IsExportedCopy(name, "Original") != match)
                throw new Exception($"Incorrect match for recovered texture {name}.");
        }
    }
}
