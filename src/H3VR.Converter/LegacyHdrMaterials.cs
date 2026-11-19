using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyHdrMaterials
{
    private static readonly Regex HdrProperty = new(
        """(?m)^[ \t]*(?<attributes>(?:\[[^\]\r\n]*\][ \t]*)+)(?<property>\w+)[ \t]*\([ \t]*"(?:\\.|[^"\\\r\n])*"[ \t]*,[ \t]*Color[ \t]*\)[ \t]*=""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ShaderReference = new(
        @"(?m)^  m_Shader: \{fileID: 4800000, guid: (?<guid>[a-fA-F0-9]{32}), type: 3\}[ \t]*\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SavedColor = new(
        @"(?m)^(?<prefix>[ \t]*(?:- )?(?<property>\w+): \{r: )(?<r>[^,\r\n]+)(?<rg>, g: )(?<g>[^,\r\n]+)(?<gb>, b: )(?<b>[^,\r\n]+)(?<suffix>, a: [^}\r\n]+\}[ \t]*)(?=\r?$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SavedColorMap = new(
        @"(?m)^    m_Colors:[ \t]*\r?\n", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LinearColorSpace = new(
        @"(?m)^  m_ActiveColorSpace: 1\r?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal sealed record ColorChange(string Property, float[] AuthoredRgb, float[] LinearRgb);
    private sealed record MaterialChange(string Asset, string ShaderGuid, string OriginalSha256,
        string MigratedSha256, ColorChange[] Colors);
    private sealed record DynamicTagChange(string Asset, string ShaderGuid, string OriginalSha256,
        string MigratedSha256, string Property);

    // Shader overlay identity has already been verified against the original
    // serialized Steam shader. ShaderLab exposes m_Type==Color and m_Flags HDR.
    internal static string[] ReadHdrColorProperties(string shader)
    {
        int pass = shader.IndexOf("\n\tSubShader", StringComparison.Ordinal);
        string header = pass >= 0 ? shader[..pass] : shader;
        return HdrProperty.Matches(header)
            .Where(match => match.Groups["attributes"].Value.Contains("[HDR]", StringComparison.Ordinal) &&
                            !match.Groups["attributes"].Value.Contains("[Gamma]", StringComparison.Ordinal))
            .Select(match => match.Groups["property"].Value).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static float GammaToLinear(float value)
    {
        // Unity 5.6's exact gamma path extends beyond the [0,1] sRGB range.
        // HDR material alpha is still linear and is never passed through here.
        if (value <= 0.04045f) return value / 12.92f;
        if (value < 1f) return MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
        return MathF.Pow(value, 2.2f);
    }

    internal static bool IsLinearProject(string root)
    {
        var settings = Path.Combine(root, "ProjectSettings", "ProjectSettings.asset");
        return File.Exists(settings) && LinearColorSpace.IsMatch(File.ReadAllText(settings));
    }

    internal static string? Apply(ConversionConfig config, string root, string assets,
        Dictionary<string, HashSet<string>> properties)
    {
        if (properties.Count == 0 || !IsLinearProject(config.Export)) return null;
        var extraction = JsonSerializer.Deserialize<StepReceipt>(File.ReadAllText(
            Path.Combine(config.Workspace, ".conversion", "extract.json")), ConversionConfig.Json)
            ?? throw new InvalidDataException("Missing extraction receipt for stock HDR material migration.");
        if (extraction.Step != "extract") throw new InvalidDataException("Invalid stock HDR material extraction receipt.");
        string originals = Path.Combine(config.Export, "Assets") + Path.DirectorySeparatorChar;
        var receipts = new List<MaterialChange>();
        var dynamicTags = new List<DynamicTagChange>();
        foreach (var (source, hash) in extraction.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!source.StartsWith(originals, StringComparison.Ordinal) ||
                !source.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(originals, source).Replace(Path.DirectorySeparatorChar, '/');
            var binding = LegacyDynamicHdrMaterials.Lookup(relative);
            byte[] originalBytes = File.ReadAllBytes(source);
            string authored = Encoding.UTF8.GetString(originalBytes);
            var shader = ShaderReference.Match(authored);
            if (!shader.Success || !properties.TryGetValue(shader.Groups["guid"].Value, out var hdrColors))
            {
                if (binding is not null)
                    throw new InvalidDataException("Missing verified shader metadata for stock dynamic HDR material: " + relative);
                continue;
            }
            string shaderGuid = shader.Groups["guid"].Value;
            string colorMigration = ConvertSavedColors(authored, hdrColors, out var colors);
            if (colors.Length == 0 && binding is null) continue;
            string candidate = SafeFiles.Inside(assets, relative);
            if (Convert.ToHexStringLower(SHA256.HashData(originalBytes)) != hash)
                throw new InvalidDataException("Original HDR material differs from the extraction receipt: " + relative);
            string migrated = colorMigration;
            if (binding is not null)
            {
                LegacyDynamicHdrMaterials.Verify(binding, relative, hash, shaderGuid, hdrColors);
                migrated = LegacyDynamicHdrMaterials.AddTag(colorMigration, binding);
            }
            var migratedBytes = Encoding.UTF8.GetBytes(migrated);
            string migratedHash = Convert.ToHexStringLower(SHA256.HashData(migratedBytes));
            string candidateHash = SafeFiles.Hash(candidate);
            if (candidateHash != hash && candidateHash != migratedHash &&
                (binding is null || candidateHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(colorMigration)))))
                throw new InvalidDataException("Candidate HDR material differs from its imported original: " + relative);
            if (candidateHash != migratedHash) File.WriteAllBytes(candidate, migratedBytes);
            if (colors.Length != 0) receipts.Add(new("Assets/" + relative, shaderGuid, hash, migratedHash, colors));
            if (binding is not null) dynamicTags.Add(new("Assets/" + relative, shaderGuid, hash, migratedHash, binding.Property));
        }

        string report = Path.Combine("NativeH3VR-RecoveredSources", "legacy-hdr-materials.json");
        string destination = SafeFiles.Inside(root, report);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(new {
            schemaVersion = 1, extractionFingerprint = extraction.Fingerprint, materials = receipts, dynamicTags
        }, ConversionConfig.Json));
        return report;
    }

    internal static string ConvertSavedColors(string material, HashSet<string> hdrColors, out ColorChange[] changes)
    {
        var map = SavedColorMap.Match(material);
        if (!map.Success)
        {
            changes = [];
            return material;
        }
        // Stock Material YAML's saved colors are the final serialized map.
        // Restrict matching to that map so untouched Vector/color-like YAML
        // elsewhere cannot be interpreted as migrated uniforms.
        int start = map.Index + map.Length;
        int end = material.IndexOf("\n  ", start, StringComparison.Ordinal);
        while (end >= 0 && end + 3 < material.Length && material[end + 3] == ' ')
            end = material.IndexOf("\n  ", end + 3, StringComparison.Ordinal);
        if (end < 0) end = material.Length;
        var edits = new List<ColorChange>();
        string colors = SavedColor.Replace(material[start..end], match =>
        {
            string property = match.Groups["property"].Value;
            if (!hdrColors.Contains(property)) return match.Value;
            float[] authored = new[] { "r", "g", "b" }.Select(channel =>
                float.Parse(match.Groups[channel].Value, CultureInfo.InvariantCulture)).ToArray();
            if (!authored.All(float.IsFinite)) return match.Value;
            var linear = authored.Select(GammaToLinear).ToArray();
            if (!linear.All(float.IsFinite)) throw new InvalidDataException("Legacy HDR color overflows: " + property);
            if (authored.AsSpan().SequenceEqual(linear)) return match.Value;
            edits.Add(new(property, authored, linear));
            string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
            return match.Groups["prefix"].Value + Format(linear[0]) + match.Groups["rg"].Value + Format(linear[1]) +
                match.Groups["gb"].Value + Format(linear[2]) + match.Groups["suffix"].Value;
        });
        changes = edits.ToArray();
        return changes.Length == 0 ? material : material[..start] + colors + material[end..];
    }
}
