namespace H3VR.Converter;

internal static class LocalShaderPreparation
{
    internal static async Task<ShaderRecoveryResult> RecoverAsync(string repository, ConversionConfig config,
        string output, CancellationToken cancellation)
    {
        var cache = config.CacheDirectory(repository);
        RequirePrivateShaderOutput(repository, config, output);
        config.ValidateToolCache(repository, config.GameDirectory, config.Workspace);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Shader output must be empty: " + output);
        Console.WriteLine("Preparing the pinned shader exporter in " + cache);
        var exporter = await ShaderToolCache.EnsureAsync(repository, cache, config.Offline, cancellation);
        // The exporter writes next to its executable. Give each run a private copy.
        var runtimeCopy = output + ".exporter-" + Guid.NewGuid().ToString("N");
        try
        {
            SafeFiles.CopyTree(Path.GetDirectoryName(exporter)!, runtimeCopy);
            return await ShaderRecovery.RecoverAsync(new(config.GameData, output,
                Path.Combine(runtimeCopy, Path.GetFileName(exporter))), cancellation);
        }
        finally
        {
            if (Directory.Exists(runtimeCopy)) Directory.Delete(runtimeCopy, true);
        }
    }

    internal static async Task<string> PrepareAsync(string repository, ConversionConfig config, CancellationToken cancellation)
    {
        var overlay = config.Workspace + "-shader-overlay";
        RequirePrivateShaderOutput(repository, config, overlay);
        if (Directory.Exists(overlay)) return overlay; // Project preparation verifies the overlay receipts.

        var database = PlayerEnlighten.RequireClassDatabase(config.CacheDirectory(repository));
        var raw = config.Workspace + "-shader-recovery";
        RequirePrivateShaderOutput(repository, config, raw);
        if (!File.Exists(Path.Combine(raw, "recovery-receipt.json")))
        {
            // A failed exporter leaves diagnostics in its own directory. A retry uses a fresh staging path.
            var staging = raw + ".attempt-" + Guid.NewGuid().ToString("N");
            await RecoverAsync(repository, config, staging, cancellation);
            Directory.Move(staging, raw);
        }
        await Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, "tools", "ShaderOverlay"),
            "-c", "Release", "--", "prepare", "--workspace", config.ExtractionWorkspace ?? config.Workspace, "--recovery", raw,
            "--steam-install", config.GameDirectory, "--output", overlay, "--class-database", database,
            "--dxbc-compiler", config.DxbcCompiler!, "--spirv-cross", config.SpirvCross!], repository, cancellation: cancellation);
        return overlay;
    }

    private static void RequirePrivateShaderOutput(string repository, ConversionConfig config, string output)
    {
        SafeFiles.RequirePrivateOutput(repository, output);
        foreach (var input in new[] { config.GameDirectory, config.CacheDirectory(repository), config.Workspace })
            SafeFiles.RequireSeparate(input, output);
    }
}
