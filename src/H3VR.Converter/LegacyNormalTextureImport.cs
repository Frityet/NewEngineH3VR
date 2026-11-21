using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyNormalTextureImport
{
    private static readonly Regex Field = new(
        @"^(?:  )?(?<key>TextureImporter|textureType|textureFormat|textureShape|alphaIsTransparency):[ \t]*(?<value>\d*)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static int Apply(string assets)
    {
        var textures = Path.Combine(assets, "Texture2D");
        if (!Directory.Exists(textures)) return 0;
        int count = 0;
        foreach (var path in Directory.EnumerateFiles(textures, "*.png.meta", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            var updated = PreserveCoordinates(source);
            if (ReferenceEquals(updated, source)) continue;
            File.WriteAllText(path, updated);
            count++;
        }
        return count;
    }

    internal static string PreserveCoordinates(string source)
    {
        var matches = Field.Matches(source);
        if (matches.Count != 5) return source;
        var fields = matches.ToLookup(m => m.Groups["key"].Value, m => m.Groups["value"]);
        if (!fields.Contains("TextureImporter") || fields["textureType"].FirstOrDefault()?.Value != "1" ||
            fields["textureFormat"].FirstOrDefault()?.Value is not ("12" or "29") ||
            fields["textureShape"].FirstOrDefault()?.Value != "1") return source;
        var flag = fields["alphaIsTransparency"].FirstOrDefault();
        if (flag?.Value != "1") return source;

        // The extractor unpacks DXT5nm's alpha/green into the PNG's red/green.
        // Its new alpha contains the old red channel, which is not coverage.
        // Dilation would replace valid tangent vectors wherever that byte is zero.
        return source[..flag.Index] + "0" + source[(flag.Index + 1)..];
    }
}
