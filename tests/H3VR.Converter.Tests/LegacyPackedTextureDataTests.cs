using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyPackedTextureDataTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("packed mask companions preserve every BC3 mip block", MipExtents),
        ("packed mask companions use exact linear BC3 data and stock sampling", NativeAsset),
        ("only exact and numbered recovered copies match the stock texture identity", ExportedCopies),
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
