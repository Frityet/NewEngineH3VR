using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyMaterialRenderTags
{
    private static readonly Regex SpeedTreeShader = new(
        @"(?m)^  m_Shader: \{fileID: 14000, guid: 0000000000000000f000000000000000, type: 0\}(?<newline>\r?\n|$)",
        RegexOptions.CultureInvariant);
    private static readonly Regex TagMap = new(
        @"(?m)^  stringTagMap:[ \t]*(?<inline>\{[^\r\n]*\})?[ \t]*(?<newline>\r?\n|$)(?<entries>(?:^    [^\r\n]*(?:\r?\n|$))*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExistingRenderType = new(
        @"(?m)(?:^    |[{,][ \t]*)(?:RenderType|""RenderType""|'RenderType')[ \t]*:",
        RegexOptions.CultureInvariant);

    internal static int Apply(string assets)
    {
        int count = 0;
        foreach (string path in SafeFiles.Files(assets).Where(path => path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
        {
            string original = File.ReadAllText(path);
            string preserved = Preserve(original);
            if (ReferenceEquals(original, preserved)) continue;
            File.WriteAllText(path, preserved);
            count++;
        }
        return count;
    }

    internal static string Preserve(string material)
    {
        // The extracted Unity 5.6 built-in Nature/SpeedTree reference resolved
        // Opaque with the authored alpha-tested queue. Newer Unity changed the
        // default render tag, which determines the game's thermal replacement.
        var shader = SpeedTreeShader.Match(material);
        if (!shader.Success) return material;
        var tags = TagMap.Match(material);
        if (tags.Success && ExistingRenderType.IsMatch(tags.Value)) return material;
        string newline = shader.Groups["newline"].Length != 0 ? shader.Groups["newline"].Value : "\n";
        if (!tags.Success)
        {
            string separator = shader.Groups["newline"].Length == 0 ? newline : "";
            return material.Insert(shader.Index + shader.Length, separator +
                "  stringTagMap:" + newline + "    RenderType: Opaque" + newline);
        }
        var folded = tags.Groups["inline"];
        if (!folded.Success)
        {
            var lineEnd = tags.Groups["newline"];
            return material.Insert(lineEnd.Index + lineEnd.Length,
                (lineEnd.Length == 0 ? newline : "") + "    RenderType: Opaque" + newline);
        }
        string entries = folded.Value[1..^1].Trim();
        return material[..folded.Index] + "{" + (entries.Length == 0 ? "" : entries + ", ") +
            "RenderType: Opaque}" + material[(folded.Index + folded.Length)..];
    }
}
