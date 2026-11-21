using System.Security.Cryptography;
using System.Text;

namespace H3VR.Converter;

internal static class StockThermalVariantProof
{
    // Steam build 25002541. SHA-256 of ordered Data-relative path/content-hash pairs.
    // The corresponding stock scene and bundle census proved that package Lit and
    // Simple Lit do not receive directional or realtime lightmaps.
    internal const string StockData = "d5ec813817c9dde3d0e259cba1540afc61874aa7a392108a23011853b32c36db";
    internal const string Marker = "stock-thermal-variant-assets.sha256";
    internal const string BuildFlag = "-h3vrVerifiedStockThermalVariants";

    internal static string Fingerprint(IEnumerable<(string Path, string ContentHash)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, contentHash) in files.Select(file => (Path: file.Path.Replace('\\', '/'), file.ContentHash))
                     .OrderBy(file => file.Path, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0" + contentHash + "\0"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static bool SupportsStockData(ConversionConfig config, string dataHash) =>
        config.GraphicsUpgrade && config.RestoresSteamLighting && dataHash == StockData &&
        Integration.KeepsStockAssetBindings(config.IntegrationManifest);

    internal static string AssetIdentity(string project)
    {
        var folder = Path.Combine(project, "Assets");
        // The normal project receipt already verifies content. Track membership
        // and importer metadata as well, since metadata can remap a renderer's materials.
        var files = new List<(string Path, string ContentHash)>();
        foreach (var path in SafeFiles.Files(folder))
        {
            bool meta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            if (meta && Directory.Exists(path[..^5])) continue;
            files.Add((Path.GetRelativePath(folder, path), meta ? SafeFiles.Hash(path) : ""));
        }
        return Fingerprint(files);
    }
}
