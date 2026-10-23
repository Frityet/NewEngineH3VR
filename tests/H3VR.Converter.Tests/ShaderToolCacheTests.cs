using System.Text.Json;
using H3VR.Converter;

namespace H3VR.Converter.Tests;

public static class ShaderToolCacheTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("shader tool cache verifies pinned provenance and every cached output", CacheIntegrity);
    }

    private static void CacheIntegrity()
    {
        var root = Path.Combine(Path.GetTempPath(), "h3vr-shader-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var commit = ShaderExporter.UpstreamCommit;
            var patch = SafeFiles.HashText(ShaderExporter.ReadSourcePatch());
            var rid = "synthetic-rid";
            var executable = Path.Combine(root, "AssetRipper.GUI.Free");
            File.WriteAllText(executable, "synthetic executable");
            File.WriteAllText(Path.Combine(root, "AssetRipper-SOURCE.md"), $"AssetRipper 1.0.15 commit {commit}\n");
            File.WriteAllText(Path.Combine(root, "AssetRipper-LICENSE.md"), "synthetic GPL license fixture\n");
            File.WriteAllText(Path.Combine(root, "AssetRipper-ShaderExporter.patch"), ShaderExporter.ReadSourcePatch());

            Check(ShaderToolCache.TryCreateManifest(root, commit, patch, rid, executable, out var manifest, out var createReason),
                "synthetic verified cache manifest could not be created: " + createReason);
            File.WriteAllText(Path.Combine(root, "h3vr-shader-tool-cache.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { IncludeFields = true }));
            Check(ShaderToolCache.TryValidate(root, commit, patch, rid, out var resolved, out var validateReason) && resolved == executable,
                "fresh synthetic cache did not validate: " + validateReason);
            File.WriteAllText(executable, "tampered executable");
            Check(!ShaderToolCache.TryValidate(root, commit, patch, rid, out _, out var changedReason) && changedReason.Contains("hash mismatch", StringComparison.Ordinal),
                "cache mutation did not fail closed with a hash mismatch");
            Check(Directory.Exists(root) && File.ReadAllText(executable) == "tampered executable",
                "invalid-cache verification deleted or rewrote existing user cache data");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
