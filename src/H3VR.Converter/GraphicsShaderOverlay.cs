using System.Text.Json;

namespace H3VR.Converter;

internal static class GraphicsShaderOverlay
{
    internal const string ReceiptName = "shader-overlay-candidate.json";

    private sealed record Link(string ProjectPath, string ProjectSha256, string ProjectMetaSha256, string RawSha256);
    private sealed record Repair(string Path, string Before, string After, Pass[] Passes);
    private sealed record Pass(Include[]? Includes);
    private sealed record Include(string Path, string Sha256);
    private sealed record Receipt(int SchemaVersion, string ValidationState, string ExtractionFingerprint,
        Link[] Links, string[] UnmappedProjectPaths, Repair[] KeywordRepairs, string? ShaderVariantCatalogSha256);

    internal static bool IsInclude(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cginc" or ".hlsl" or ".glslinc";

    internal static (string Path, string Sha256)? Verify(ConversionConfig config, string overlayRoot, string overlayAssets,
        string[] shaders, string[] includes)
    {
        var receipt = Read<Receipt>(Path.Combine(overlayRoot, ReceiptName));
        var extraction = Read<StepReceipt>(Path.Combine(config.Workspace, ".conversion", "extract.json"));
        if (receipt.SchemaVersion != 1 || receipt.ValidationState != "identity-only-unvalidated" ||
            extraction.Step != "extract" || receipt.ExtractionFingerprint != extraction.Fingerprint)
            throw new InvalidDataException("Graphics shader overlay does not match this local Steam extraction.");
        if (receipt.UnmappedProjectPaths is not { Length: 0 } || receipt.Links is not { Length: > 0 } ||
            receipt.KeywordRepairs is null)
            throw new InvalidDataException("Graphics shader overlay has incomplete correspondence.");

        var shaderFiles = shaders.ToHashSet(StringComparer.Ordinal);
        var includeFiles = includes.ToHashSet(StringComparer.Ordinal);
        var exportedAssets = Path.Combine(config.Export, "Assets") + Path.DirectorySeparatorChar;
        var originals = extraction.Files.Keys.Where(path => path.StartsWith(exportedAssets, StringComparison.Ordinal)
            && path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var repairs = new Dictionary<string, Repair>(StringComparer.Ordinal);
        foreach (var repair in receipt.KeywordRepairs)
            if (!repairs.TryAdd(repair.Path, repair) || repair.Passes is not { Length: > 0 })
                throw new InvalidDataException("Duplicate or empty graphics shader repair: " + repair.Path);

        foreach (var link in receipt.Links)
        {
            var original = SafeFiles.Inside(config.Export, link.ProjectPath);
            if (!originals.Remove(original) || extraction.Files.GetValueOrDefault(original) != link.ProjectSha256 ||
                extraction.Files.GetValueOrDefault(original + ".meta") != link.ProjectMetaSha256)
                throw new InvalidDataException("Graphics shader does not match an original extraction file: " + link.ProjectPath);

            var path = Consume(overlayAssets, link.ProjectPath, shaderFiles);
            var expected = link.RawSha256;
            if (repairs.Remove(link.ProjectPath, out var repair))
            {
                if (repair.Before != expected)
                    throw new InvalidDataException("Graphics shader repair does not match the raw export: " + repair.Path);
                expected = repair.After;
                foreach (var include in repair.Passes.SelectMany(pass => pass.Includes ?? []))
                    RequireHash(Consume(overlayAssets, include.Path, includeFiles), include.Sha256);
            }
            RequireHash(path, expected);
            RequireHash(path + ".meta", link.ProjectMetaSha256);
        }

        if (repairs.Count + shaderFiles.Count + includeFiles.Count != 0)
            throw new InvalidDataException("Graphics shader overlay contains unrecorded shaders or includes.");
        if (originals.Count != 0)
            throw new InvalidDataException("Graphics shader overlay is missing original shaders from this extraction.");

        var catalog = SafeFiles.Inside(overlayRoot, SourceStaging.ShaderVariantCatalogName);
        var catalogFile = new FileInfo(catalog);
        if (receipt.ShaderVariantCatalogSha256 is null)
        {
            if (catalogFile.Exists || Directory.Exists(catalog) || catalogFile.LinkTarget is not null)
                throw new InvalidDataException("Graphics shader overlay has an unrecorded variant catalog.");
            return null;
        }
        if (!catalogFile.Exists || catalogFile.LinkTarget is not null)
            throw new InvalidDataException("Graphics shader variant catalog is missing or linked.");
        RequireHash(catalog, receipt.ShaderVariantCatalogSha256);
        return (catalog, receipt.ShaderVariantCatalogSha256);
    }

    private static string Consume(string assets, string relative, HashSet<string> expected)
    {
        if (!relative.StartsWith("Assets/", StringComparison.Ordinal))
            throw new InvalidDataException("Expected a project-relative graphics shader path: " + relative);
        var path = SafeFiles.Inside(assets, relative["Assets/".Length..]);
        if (!expected.Remove(path))
            throw new InvalidDataException("Missing or duplicate graphics shader file: " + relative);
        return path;
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), ConversionConfig.Json)
        ?? throw new InvalidDataException("Empty graphics shader receipt: " + path);

    private static void RequireHash(string path, string expected)
    {
        if (SafeFiles.Hash(path) != expected)
            throw new InvalidDataException("Graphics shader receipt hash differs: " + path);
    }
}
