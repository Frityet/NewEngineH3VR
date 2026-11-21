using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

internal static class LegacySignTextureMips
{
    private const string AdditiveShader = "a93930d1adfa66c45b9aa2fd5bf93c02";
    private const string StockFile = "sharedassets18.assets";
    private const RegexOptions Line = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    private static readonly Regex GuidField = new(@"^guid: (?<guid>[a-f0-9]{32})\r?$", Line);
    private static readonly Regex MainTexture = new(
        @"^      (?:- )?_MainTex:\r?\n        m_Texture: \{fileID: 2800000, guid: (?<guid>[a-f0-9]{32}), type: (?<type>3)\}\r?$", Line);

    private sealed record Sign(string Material, string MaterialGuid, string ImportGuid, LegacyPackedTextureData.StockTexture Texture);
    private static readonly Sign[] Signs =
    [
        new("TAH_TouchToBegin", "d270086c0f2943a4b9ee320d8a3af014", "25fd9307a0847d644aa971888b915ff0",
            new("TAH_TouchToBegin", StockFile, 36, "63a14f95b23c4a0e0b8ad8b62049063ae3640c3fb6c86ef343952ca80a034770", 0, 1)),
        new("TAH_HoldPoint", "7546a968d5b2a0b49bc5b8fb34c32f58", "ff5c737ffb2e92245a9323ce4cbfee38",
            new("TAH_HoldPointSign", StockFile, 37, "14910266fa1a79f95b4dcd365ef7bb73c3111b6a9c12c82aaef2166d697b403c", 0, 1))
    ];

    internal static int Stage(string source, string assets, string classDatabase)
    {
        int count = 0;
        AssetsManager? manager = null;
        foreach (var sign in Signs)
        {
            string materialPath = Path.Combine(assets, "Material", sign.Material + ".mat");
            string textureMeta = Path.Combine(assets, "Texture2D", sign.Texture.Name + ".png.meta");
            if (!File.Exists(materialPath) || !HasGuid(materialPath + ".meta", sign.MaterialGuid) ||
                !HasGuid(textureMeta, sign.ImportGuid))
                continue;

            string material = File.ReadAllText(materialPath);
            string derivedGuid = sign.Texture.Sha256[..32];
            string? migrated = RewriteBinding(material, sign.Material, sign.ImportGuid, derivedGuid);
            if (migrated is null) continue;

            if (manager is null)
            {
                manager = new AssetsManager();
                manager.LoadClassPackage(classDatabase);
            }
            var texture = LegacyPackedTextureData.ReadSource(manager, source, sign.Texture);
            if (texture.Width != 1024 || texture.Height != 256)
                throw new InvalidDataException($"Unexpected stock sign dimensions for {sign.Texture.Name}.");

            // These additive signs already ship their transparency-filtered mip chain.
            // Importing just mip zero generates much brighter small text and outlines.
            var output = Path.Combine(assets, "NativeH3VR-Derived", "StockSignTextures");
            LegacyPackedTextureData.WriteNativeTexture(output, sign.Texture.Name, derivedGuid,
                texture with { IgnoreMipmapLimit = false });
            File.WriteAllText(materialPath, migrated);
            count++;
        }
        return count;
    }

    private static bool HasGuid(string path, string expected) =>
        File.Exists(path) && GuidField.Match(File.ReadAllText(path)).Groups["guid"].Value == expected;

    internal static string? RewriteBinding(string material, string name, string importerGuid, string derivedGuid)
    {
        if (!Regex.IsMatch(material, @"^  m_Name: " + Regex.Escape(name) + @"\r?$", Line) ||
            !Regex.IsMatch(material, @"^  m_Shader: \{fileID: 4800000, guid: " + AdditiveShader + @", type: 3\}\r?$", Line))
            return null;

        var reference = MainTexture.Match(material);
        var guid = reference.Groups["guid"];
        var type = reference.Groups["type"];
        return guid.Value == importerGuid
            ? material[..guid.Index] + derivedGuid + material[(guid.Index + guid.Length)..type.Index] +
                "2" + material[(type.Index + type.Length)..]
            : null;
    }
}
