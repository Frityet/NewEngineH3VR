using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyTextureImport
{
    private static readonly Regex Field = new(
        @"^(?:  )?(?<key>TextureImporter|textureType|textureFormat|textureShape|alphaIsTransparency):[ \t]*(?<value>\d*)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex DefaultPlatformCompression = new(
        @"^  - buildTarget: DefaultTexturePlatform\r?\n(?:    [^\r\n]*\r?\n)*?    textureFormat: -1\r?\n    textureCompression: (?<quality>[12])\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static int Apply(string assets)
    {
        var textures = Path.Combine(assets, "Texture2D");
        if (!Directory.Exists(textures)) return 0;
        int count = 0;
        foreach (var path in Directory.EnumerateFiles(textures, "*.png.meta", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            var updated = PreserveStockFormat(source);
            if (ReferenceEquals(updated, source)) continue;
            File.WriteAllText(path, updated);
            count++;
        }
        return count;
    }

    internal static string PreserveStockFormat(string source)
    {
        var matches = Field.Matches(source);
        if (matches.Count != 5) return source;
        var fields = matches.ToLookup(m => m.Groups["key"].Value, m => m.Groups["value"]);
        if (!fields.Contains("TextureImporter") || fields["textureShape"].FirstOrDefault()?.Value != "1") return source;
        string? type = fields["textureType"].FirstOrDefault()?.Value;
        string? format = fields["textureFormat"].FirstOrDefault()?.Value;
        if (type == "0" && format is "3" or "4" or "5")
        {
            // Recovery sets high-quality block compression even on uncompressed RGB24,
            // RGBA32 and ARGB32 assets. A block cannot reproduce a stock random-colour lookup.
            var quality = DefaultPlatformCompression.Match(source).Groups["quality"];
            return quality.Success ? source[..quality.Index] + "0" + source[(quality.Index + 1)..] : source;
        }
        var flag = fields["alphaIsTransparency"].FirstOrDefault();
        if (type != "1" || format is not ("12" or "29") || flag?.Value != "1") return source;

        // The extractor unpacks DXT5nm's alpha/green into the PNG's red/green.
        // Its new alpha contains the old red channel, which is not coverage.
        // Dilation would replace valid tangent vectors wherever that byte is zero.
        return source[..flag.Index] + "0" + source[(flag.Index + 1)..];
    }
}
