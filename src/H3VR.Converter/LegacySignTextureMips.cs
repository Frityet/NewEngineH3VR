using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

internal static class LegacySignTextureMips
{
    private const string AdditiveShader = "Alloy/Particles/Additive";
    private const string PremultipliedShader = "Alloy/Unlit";
    private const string HoldFile = "sharedassets18.assets";
    private const string WarningFile = "sharedassets12.assets";
    private const RegexOptions Line = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    private static readonly Regex GuidField = new(@"^guid: (?<guid>[a-f0-9]{32})\r?$", Line);
    private static readonly Regex MaterialShader = new(@"^  m_Shader: \{fileID: 4800000, guid: (?<guid>[a-f0-9]{32}), type: 3\}\r?$", Line);
    private static readonly Regex MainTexture = new(
        @"^      (?:- )?_MainTex:\r?\n        m_Texture: \{fileID: 2800000, guid: (?<guid>[a-f0-9]{32}), type: (?<type>3)\}\r?$", Line);

    private sealed record Sign(string Material, string Shader, int Height, LegacyPackedTextureData.StockTexture Texture);
    private static readonly Sign[] Signs =
    [
        new("TAH_TouchToBegin", AdditiveShader, 256,
            new("TAH_TouchToBegin", HoldFile, 36, "63a14f95b23c4a0e0b8ad8b62049063ae3640c3fb6c86ef343952ca80a034770", 0, 1)),
        new("TAH_HoldPoint", AdditiveShader, 256,
            new("TAH_HoldPointSign", HoldFile, 37, "14910266fa1a79f95b4dcd365ef7bb73c3111b6a9c12c82aaef2166d697b403c", 0, 1)),
        new("MG_4Plinth_Warning1", PremultipliedShader, 128,
            new("MG_4Plinth_Warning1", WarningFile, 195, "98811fc73d3ed644ce25032f2da236646b850b218e626840d91a1fdaca683616", 0, 1)),
        new("MG_4Plinth_Warning2", PremultipliedShader, 128,
            new("MG_4Plinth_Warning2", WarningFile, 187, "c8cdea71db707a3a47598d4df8f108275c957c715ebacce86b5c05435ad76d1a", 0, 1))
    ];

    internal static int Stage(string source, string assets, string classDatabase)
    {
        int count = 0;
        AssetsManager? manager = null;
        var shaders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [AdditiveShader] = new(StringComparer.Ordinal),
            [PremultipliedShader] = new(StringComparer.Ordinal)
        };
        foreach (string path in SafeFiles.Files(assets).Where(path =>
            Path.GetExtension(path).Equals(".shader", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = File.OpenText(path);
            string? header;
            do { header = reader.ReadLine()?.Trim(); }
            while (header is not null && (header.Length == 0 || header.StartsWith("//", StringComparison.Ordinal)));
            foreach (var (name, guids) in shaders)
                if ((header == $"Shader \"{name}\" {{" || header == $"Shader \"{name}\"") && ReadGuid(path + ".meta") is { } guid)
                    guids.Add(guid);
        }

        foreach (var sign in Signs)
        {
            string materialPath = Path.Combine(assets, "Material", sign.Material + ".mat");
            string textureMeta = Path.Combine(assets, "Texture2D", sign.Texture.Name + ".png.meta");
            string? importerGuid = ReadGuid(textureMeta);
            if (!File.Exists(materialPath) || ReadGuid(materialPath + ".meta") is null || importerGuid is null)
                continue;

            string material = File.ReadAllText(materialPath);
            string derivedGuid = sign.Texture.Sha256[..32];
            string? migrated = RewriteBinding(material, sign.Material, importerGuid, derivedGuid, shaders[sign.Shader],
                premultiplied: sign.Shader == PremultipliedShader);
            if (migrated is null) continue;

            if (manager is null)
            {
                manager = new AssetsManager();
                manager.LoadClassPackage(classDatabase);
            }
            var texture = LegacyPackedTextureData.ReadSource(manager, source, sign.Texture);
            if (texture.Width != 1024 || texture.Height != sign.Height)
                throw new InvalidDataException($"Unexpected stock sign dimensions for {sign.Texture.Name}.");

            // The published signs already carry filtered additive or premultiplied
            // RGB. Dilation and regenerated mips brighten the text and its margins.
            var output = Path.Combine(assets, "NativeH3VR-Derived", "StockSignTextures");
            LegacyPackedTextureData.WriteNativeTexture(output, sign.Texture.Name, derivedGuid,
                texture with { IgnoreMipmapLimit = false });
            File.WriteAllText(materialPath, migrated);
            count++;
        }
        return count;
    }

    private static string? ReadGuid(string path)
    {
        if (!File.Exists(path)) return null;
        var match = GuidField.Match(File.ReadAllText(path));
        return match.Success ? match.Groups["guid"].Value : null;
    }

    internal static string? RewriteBinding(string material, string name, string importerGuid, string derivedGuid,
        IReadOnlySet<string> shaderGuids, bool premultiplied = false)
    {
        if (!Regex.IsMatch(material, @"^  m_Name: " + Regex.Escape(name) + @"\r?$", Line) ||
            !shaderGuids.Contains(MaterialShader.Match(material).Groups["guid"].Value))
            return null;

        if (premultiplied &&
            (!Regex.IsMatch(material, @"^  m_ShaderKeywords:[^\r\n]*\b_ALPHAPREMULTIPLY_ON\b", Line) ||
             !Regex.IsMatch(material, @"^      (?:- )?_SrcBlend: 1\r?$", Line) ||
             !Regex.IsMatch(material, @"^      (?:- )?_DstBlend: 10\r?$", Line)))
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
