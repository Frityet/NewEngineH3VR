using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyTextureAlpha
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
    private static readonly Regex GuidField = new(@"^guid: (?<guid>[a-f0-9]{32})\r?$", Options);
    private static readonly Regex ShaderName = new("^Shader[\t ]+\"(?<name>Alloy/(?:Core|Oriented/Core|Prototyping|Car Paint|Unlit))\"", Options);
    private static readonly Regex Shader = new(@"^  m_Shader: \{fileID: 4800000, guid: (?<guid>[a-f0-9]{32}), type: 3\}\r?$", Options);
    private static readonly Regex Keywords = new(@"^  m_ShaderKeywords: *(?<keywords>[^\r\n]*)", Options);
    private static readonly Regex Opaque = new(@"^      (?:- )?_Mode: 0\r?$", Options);
    private static readonly Regex SourceBlend = new(@"^      (?:- )?_SrcBlend: 1\r?$", Options);
    private static readonly Regex DestinationBlend = new(@"^      (?:- )?_DstBlend: 0\r?$", Options);
    private static readonly Regex Queue = new(@"^  m_CustomRenderQueue: (?<queue>-?[0-9]+)\r?$", Options);
    private static readonly Regex TextureSlot = new(
        @"^      (?:- )?(?<slot>_\w+):\r?\n        m_Texture: \{fileID: 2800000, guid: (?<guid>[a-f0-9]{32}), type: 3\}",
        Options);
    private static readonly Regex GuidReference = new(@", guid: (?<guid>[a-f0-9]{32}), type:", Options);
    private static readonly Regex Importer = new(@"^TextureImporter:\r?$", Options);
    private static readonly Regex DefaultTexture = new(@"^  textureType: 0\r?$", Options);
    private static readonly Regex Dilation = new(@"^  alphaIsTransparency: (?<flag>1)\r?$", Options);
    private static readonly HashSet<string> AlphaCoverageKeywords = new(StringComparer.Ordinal)
    {
        "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON"
    };

    internal static string? ReadDataShader(string source)
    {
        var match = ShaderName.Match(source);
        return match.Success ? match.Groups["name"].Value : null;
    }

    internal static int Apply(string assets, IReadOnlyDictionary<string, string> verifiedShaders)
    {
        if (verifiedShaders.Count == 0) return 0;

        var dataTextures = new HashSet<string>(StringComparer.Ordinal);
        var otherUses = new HashSet<string>(StringComparer.Ordinal);
        var paths = SafeFiles.Files(assets).ToArray();
        foreach (string path in paths.Where(path => path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
        {
            string material = File.ReadAllText(path);
            verifiedShaders.TryGetValue(Shader.Match(material).Groups["guid"].Value, out string? shader);
            var keywords = Keywords.Match(material).Groups["keywords"].Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            bool opaque = shader is not null && Opaque.IsMatch(material) && SourceBlend.IsMatch(material) &&
                DestinationBlend.IsMatch(material) && !keywords.Overlaps(AlphaCoverageKeywords) &&
                int.TryParse(Queue.Match(material).Groups["queue"].Value, out int queue) && queue <= 2500;
            var dataReferences = new HashSet<int>();
            foreach (Match slot in TextureSlot.Matches(material))
            {
                if (!opaque || !IsDataSlot(shader!, slot.Groups["slot"].Value, keywords)) continue;
                var guid = slot.Groups["guid"];
                dataReferences.Add(guid.Index);
                dataTextures.Add(guid.Value);
            }
            foreach (Match reference in GuidReference.Matches(material))
            {
                var guid = reference.Groups["guid"];
                if (!dataReferences.Contains(guid.Index)) otherUses.Add(guid.Value);
            }
        }

        dataTextures.ExceptWith(otherUses);
        if (dataTextures.Count == 0) return 0;

        // A texture may also be serialized directly as UI, a sprite or a script
        // input. Only change assets whose references are exclusively data slots.
        foreach (string path in paths.Where(path => Path.GetExtension(path).ToLowerInvariant() is
            ".unity" or ".prefab" or ".asset" or ".controller" or ".overridecontroller" or ".anim" or ".spriteatlas" or ".playable" or ".meta"))
        {
            foreach (Match reference in GuidReference.Matches(File.ReadAllText(path)))
                dataTextures.Remove(reference.Groups["guid"].Value);
        }

        int count = 0;
        string textures = Path.Combine(assets, "Texture2D") + Path.DirectorySeparatorChar;
        foreach (string path in paths.Where(path => path.StartsWith(textures, StringComparison.Ordinal) &&
            path.EndsWith(".png.meta", StringComparison.OrdinalIgnoreCase)))
        {
            string importer = File.ReadAllText(path);
            if (!dataTextures.Contains(GuidField.Match(importer).Groups["guid"].Value) ||
                !Importer.IsMatch(importer) || !DefaultTexture.IsMatch(importer))
                continue;
            var flag = Dilation.Match(importer).Groups["flag"];
            if (!flag.Success) continue;
            // The decoded Steam image already has its authored RGB padding.
            File.WriteAllText(path, importer[..flag.Index] + "0" + importer[(flag.Index + 1)..]);
            count++;
        }
        return count;
    }

    private static bool IsDataSlot(string shader, string slot, HashSet<string> keywords) => slot switch
    {
        // Opaque draws read albedo RGB even where source alpha is zero.
        "_MainTex" => shader is "Alloy/Core" or "Alloy/Car Paint" or "Alloy/Prototyping" or "Alloy/Unlit",
        "_SpecTex" => shader is "Alloy/Core" or "Alloy/Car Paint",
        "_OrientedMainTex" => shader == "Alloy/Oriented/Core",
        "_OrientedMaterialMap" => shader == "Alloy/Oriented/Core",
        "_AoMap" => shader == "Alloy/Prototyping",
        "_DetailAlbedoMap" => (shader is "Alloy/Core" or "Alloy/Car Paint" or "Alloy/Prototyping") && keywords.Contains("_DETAIL_MULX2"),
        "_TeamColorMaskMap" => (shader is "Alloy/Core" or "Alloy/Car Paint" or "Alloy/Prototyping") && keywords.Contains("_TEAMCOLOR_ON"),
        // RGB effect data remains RGB if the material enables its emission pass at runtime.
        "_IncandescenceMap" => shader is "Alloy/Core" or "Alloy/Car Paint" or "Alloy/Prototyping",
        _ => false
    };
}
