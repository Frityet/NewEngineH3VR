using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class AssetRipperIntegrationTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("AssetRipper worker exposes a fixed public pin", () =>
        {
            Check(AssetRipperIntegration.UpstreamCommit == "1ac666f47d8e9dedf96afb0b914c70d7656151ea", "unexpected source pin");
            Check(AssetRipperIntegration.SubmodulePath == "extern/AssetRipper", "unexpected submodule path");
            Check(AssetRipperIntegration.UpstreamUrl == "https://github.com/AssetRipper/AssetRipper.git", "unexpected source URL");
        }
        );

        yield return ("offline availability check does not bootstrap or create cache", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "assetripper-offline-" + Guid.NewGuid().ToString("N"));
            var config = new ConversionConfig { Offline = true, ToolCacheDirectory = Path.Combine(root, "cache") };
            try
            {
                var available = AssetRipperIntegration.CheckAvailability(config, Repository.Find(), out var detail);
                Check(!available && detail.Contains("Offline", StringComparison.Ordinal), "missing cache was not reported");
                Check(!Directory.Exists(root), "availability check mutated the cache location");
                ThrowsAsync(() => AssetRipperIntegration.ExportAsync(Repository.Find(), config,
                    Path.Combine(root, "output"), Path.Combine(root, "log.txt"), CancellationToken.None));
                Check(!Directory.Exists(root), "offline cache miss created files");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        );

        yield return ("AssetRipper availability validates repository metadata", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "assetripper-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, ".gitmodules"),
                    "[submodule \"extern/AssetRipper\"]\n\tpath = extern/AssetRipper\n\turl = https://example.invalid/AssetRipper.git\n");
                var config = new ConversionConfig();
                Check(!AssetRipperIntegration.CheckAvailability(config, root, out var detail), "noncanonical source URL was accepted");
                Check(detail.Contains("Expected extern/AssetRipper", StringComparison.Ordinal), "metadata error is not actionable");
            }
            finally { Directory.Delete(root, true); }
        }
        );

        yield return ("missing submodule availability check is read-only", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "assetripper-uninitialized-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, ".gitmodules"),
                    $"[submodule \"{AssetRipperIntegration.SubmodulePath}\"]\n\tpath = {AssetRipperIntegration.SubmodulePath}\n\turl = {AssetRipperIntegration.UpstreamUrl}\n");
                var available = AssetRipperIntegration.CheckAvailability(new ConversionConfig(), root, out var detail);
                Check(available && detail.Contains("first extraction", StringComparison.Ordinal), "online doctor did not describe deferred initialization");
                Check(!Directory.Exists(Path.Combine(root, AssetRipperIntegration.SubmodulePath)), "read-only check initialized the submodule");
            }
            finally { Directory.Delete(root, true); }
        }
        );
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ThrowsAsync(Func<Task> action)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (InvalidOperationException error) when (error.Message.Contains("Offline mode", StringComparison.Ordinal)) { return; }
        throw new Exception("Expected an actionable offline cache error");
    }
}
