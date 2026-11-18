using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static partial class ComputeShaderOverlay
{
    internal const string DirectoryName = "ComputeShader";
    internal const string ReceiptName = "compute-provenance.json";

    private sealed record Entry(string Source, string SourceSha256, string SourceMetaSha256,
        string Compute, string ComputeSha256, string ComputeMetaSha256, Kernel[] Kernels);
    private sealed record Kernel(string Name);
    private sealed record Receipt(int SchemaVersion, string ExtractionFingerprint, Entry[] Files);

    [GeneratedRegex(@"(?m)^guid: ([0-9a-f]{32})$")]
    private static partial Regex GuidPattern();
    [GeneratedRegex(@"(?m)^\s*#pragma[ \t]+kernel[ \t]+(\w+)")]
    private static partial Regex KernelPattern();
    [GeneratedRegex(@"\{fileID:\s*7200000,\s*guid:\s*([0-9a-f]{32}),\s*type:\s*2\}")]
    private static partial Regex References();

    internal static HashSet<string> Stage(ConversionConfig config, string overlayAssets, string targetAssets)
    {
        var guids = new HashSet<string>(StringComparer.Ordinal);
        var sources = SafeFiles.Files(overlayAssets).Where(p => p.EndsWith(".compute", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        if (sources.Count == 0) return guids;

        var overlay = Path.Combine(overlayAssets, DirectoryName);
        var target = Path.Combine(targetAssets, DirectoryName);
        var receipt = Read<Receipt>(Path.Combine(overlay, ReceiptName));
        var extract = Read<StepReceipt>(Path.Combine(config.Workspace, ".conversion", "extract.json"));
        if (receipt.SchemaVersion != 1 || extract.Step != "extract" || extract.Fingerprint != receipt.ExtractionFingerprint)
            throw new InvalidDataException("Compute shader overlay was not recovered from this extraction.");

        foreach (var input in receipt.Files)
        {
            if (Path.GetFileName(input.Source) != input.Source || !input.Source.EndsWith(".asset", StringComparison.Ordinal) ||
                Path.ChangeExtension(input.Source, ".compute") != input.Compute)
                throw new InvalidDataException("Invalid compute source path.");
            var source = SafeFiles.Inside(overlay, input.Compute);
            if (!sources.Remove(source)) throw new InvalidDataException("Missing or duplicate compute output: " + source);
            var old = SafeFiles.Inside(target, input.Source);
            var output = SafeFiles.Inside(target, input.Compute);
            var exported = Path.Combine(config.Export, "Assets", DirectoryName, input.Source);
            if (extract.Files.GetValueOrDefault(exported) != input.SourceSha256 ||
                extract.Files.GetValueOrDefault(exported + ".meta") != input.SourceMetaSha256)
                throw new InvalidDataException("Compute source is not recorded in this extraction: " + input.Source);
            RequireHash(old, input.SourceSha256);
            RequireHash(old + ".meta", input.SourceMetaSha256);
            RequireHash(source, input.ComputeSha256);
            RequireHash(source + ".meta", input.ComputeMetaSha256);

            var originalMetadata = File.ReadAllText(old + ".meta");
            var metadata = File.ReadAllText(source + ".meta");
            var guid = GuidPattern().Match(originalMetadata).Groups[1].Value;
            if (guid.Length == 0 || !originalMetadata.Contains("  mainObjectFileID: 7200000", StringComparison.Ordinal) ||
                GuidPattern().Match(metadata).Groups[1].Value != guid || !metadata.Contains("\nComputeShaderImporter:\n", StringComparison.Ordinal) ||
                !guids.Add(guid))
                throw new InvalidDataException("Compute importer metadata does not preserve a unique original GUID: " + input.Source);
            var kernels = KernelPattern().Matches(File.ReadAllText(source)).Select(m => m.Groups[1].Value).ToArray();
            if (kernels.Length == 0 || kernels.Distinct(StringComparer.Ordinal).Count() != kernels.Length ||
                !kernels.SequenceEqual(input.Kernels.Select(kernel => kernel.Name)))
                throw new InvalidDataException("Translated compute kernels differ from the recovery receipt: " + input.Source);

            SafeFiles.Copy(source, output);
            SafeFiles.Copy(source + ".meta", output + ".meta");
            RequireHash(output, input.ComputeSha256);
            RequireHash(output + ".meta", input.ComputeMetaSha256);
            File.Delete(old);
            File.Delete(old + ".meta");
        }
        if (sources.Count != 0) throw new InvalidDataException("Compute overlay contains unrecorded sources.");
        return guids;
    }

    internal static string RewriteReferences(string text, HashSet<string> guids) =>
        References().Replace(text, match => guids.Contains(match.Groups[1].Value)
            ? $"{{fileID: 7200000, guid: {match.Groups[1].Value}, type: 3}}" : match.Value);

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), ConversionConfig.Json)
        ?? throw new InvalidDataException("Empty compute recovery receipt: " + path);

    private static void RequireHash(string path, string expected)
    {
        if (SafeFiles.Hash(path) != expected) throw new InvalidDataException("Compute recovery hash differs: " + path);
    }
}
