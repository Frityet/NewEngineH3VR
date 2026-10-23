using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

/// <summary>
/// Reproduces the raw H3VR shader-source export stage. The returned project is an
/// unvalidated AssetRipper export and must pass the separate Unity/semantic gates
/// before it is supplied as ShaderSources to project migration.
/// </summary>
public sealed record ShaderRecoveryOptions(
    string GameData,
    string OutputDirectory,
    string AssetRipperExecutable,
    string ServiceUrl = "http://127.0.0.1:58948/",
    bool StartAssetRipper = true);

public sealed record ShaderRecoveryResult(
    string OutputDirectory,
    string AssetInputDirectory,
    int InputAssetFiles,
    int ShaderObjects,
    int ExportedShaders,
    string ValidationState);

public static class ShaderRecovery
{
    private const string AssetRipperSettings = """
    {
      "IgnoreStreamingAssets": "on",
      "DefaultVersion": "0.0.0a0",
      "BundledAssetsExportMode": "DirectExport",
      "ScriptContentLevel": "Level2",
      "TargetVersion": "0.0.0a0",
      "AudioExportFormat": "Default",
      "ImageExportFormat": "Png",
      "LightmapTextureExportFormat": "Yaml",
      "SpriteExportMode": "Yaml",
      "ShaderExportMode": "Decompile",
      "TextExportMode": "Parse",
      "ScriptLanguageVersion": "CSharp7_3",
      "ScriptExportMode": "Hybrid",
      "PreferOriginalTextureExtension": "on",
      "ExportUnreadableAssets": "on",
      "EnableStaticMeshSeparation": "on",
      "TerrainExportMode": "Yaml",
      "MeshExportFormat": "Native"
    }
    """;

    public static async Task<ShaderRecoveryResult> RecoverAsync(
        ShaderRecoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        var gameData = Path.GetFullPath(options.GameData);
        var output = Path.GetFullPath(options.OutputDirectory);
        var ripper = Path.GetFullPath(options.AssetRipperExecutable);
        if (!Directory.Exists(gameData)) throw new DirectoryNotFoundException($"Game data directory not found: {gameData}");
        if (!File.Exists(ripper)) throw new FileNotFoundException("AssetRipper executable is missing.", ripper);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException($"Refusing to overwrite non-empty recovery output: {output}");
        if (IsWithin(output, gameData) || IsWithin(gameData, output))
            throw new IOException("Recovery output and installed game data must be separate directories.");
        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUri) ||
            serviceUri.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(serviceUri.Host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("ServiceUrl must be an HTTP loopback URL.");
        await RequireAssetRipperVersion(ripper, cancellationToken);

        Directory.CreateDirectory(output);
        var inputs = Path.Combine(output, "shader-inputs");
        var export = Path.Combine(output, "assetripper-export");
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(export);
        var inventoryPath = Path.Combine(output, "shader-input-manifest.json");
        var (inputFiles, shaderObjects) = ExtractShaderAssets(gameData, inputs, inventoryPath, cancellationToken);

        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath, cancellationToken));
        if (inputFiles == 0 || shaderObjects == 0) throw new InvalidDataException("No serialized shader assets were found in the installed game.");

        Process? server = null;
        try
        {
            if (options.StartAssetRipper)
            {
                server = Process.Start(new ProcessStartInfo(ripper)
                {
                    WorkingDirectory = Path.GetDirectoryName(ripper)!,
                    UseShellExecute = false,
                    ArgumentList = { "--port", serviceUri.Port.ToString(), "--launch-browser", "false" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Could not start AssetRipper.");
            }
            await WaitForService(serviceUri, server, cancellationToken);
            using var settingsDocument = JsonDocument.Parse(AssetRipperSettings);
            var settingsForm = string.Join("&", settingsDocument.RootElement.EnumerateObject()
                .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.ToString())));
            await PostForm(serviceUri, "Settings/Update", settingsForm, cancellationToken);

            var assets = Directory.EnumerateFiles(inputs, "*.assets", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var loadForm = string.Join("&", assets.Select(p => "Path=" + Uri.EscapeDataString(p)));
            await PostForm(serviceUri, "LoadFile", loadForm, cancellationToken);
            await PostForm(serviceUri, "Export", "Path=" + Uri.EscapeDataString(export), cancellationToken);
        }
        finally
        {
            if (server is { HasExited: false })
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync(CancellationToken.None);
            }
            server?.Dispose();
        }

        var projectAssets = Path.Combine(export, "ExportedProject", "Assets");
        var shaderFiles = Directory.Exists(projectAssets)
            ? Directory.EnumerateFiles(projectAssets, "*.shader", SearchOption.AllDirectories).ToArray()
            : [];
        var accepted = shaderFiles.Where(IsSourceShader).ToArray();
        if (accepted.Length == 0) throw new InvalidDataException("AssetRipper produced no non-dummy .shader source files.");

        var receipt = new
        {
            schemaVersion = 1,
            exporter = "AssetRipper 1.0.15-compatible local build",
            generatedUtc = DateTimeOffset.UtcNow,
            gameDataPath = gameData,
            shaderObjects,
            exportedShaderFiles = shaderFiles.Length,
            usableSourceFiles = accepted.Length,
            validationState = "raw-export-unvalidated",
            limitation = "No Unity compilation, original-program equivalence, variant coverage, Metal pipeline, or visual validation is implied.",
            inputManifest = Path.GetFileName(inventoryPath),
            sha256 = accepted.OrderBy(p => p, StringComparer.Ordinal).ToDictionary(
                p => Path.GetRelativePath(export, p).Replace('\\', '/'),
                p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant(),
                StringComparer.Ordinal)
        };
        await File.WriteAllTextAsync(Path.Combine(output, "recovery-receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return new ShaderRecoveryResult(output, inputs, inputFiles, shaderObjects, accepted.Length, "raw-export-unvalidated");
    }

    private static (int InputFiles, int ShaderObjects) ExtractShaderAssets(string gameData, string output, string manifestPath, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        paths.AddRange(Directory.EnumerateFiles(gameData).Where(p => Path.GetFileName(p) == "globalgamemanagers" || Path.GetExtension(p) == ".assets" || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^level\\d+$")));
        foreach (var directory in new[] { Path.Combine(gameData, "Resources"), Path.Combine(gameData, "StreamingAssets") })
            if (Directory.Exists(directory)) paths.AddRange(Directory.EnumerateFiles(directory));
        paths = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var records = new List<object>();
        var totalShaders = 0;
        var index = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var beforeHash = HashFile(path);
            var relative = Path.GetRelativePath(gameData, path).Replace(Path.DirectorySeparatorChar, '/');
            var baseName = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(relative), "[^A-Za-z0-9_.-]+", "_");
            var extracted = 0;
            if (AssetsFile.IsAssetsFile(path))
            {
                using var input = File.OpenRead(path);
                using var reader = new AssetsFileReader(input);
                var assets = new AssetsFile();
                assets.Read(reader);
                extracted = WriteShaderOnlyCopy(assets, output, index, baseName, records, beforeHash, relative);
            }
            else if (IsUnityFs(path))
            {
                var manager = new AssetsManager();
                try
                {
                    var bundle = manager.LoadBundleFile(path);
                    var count = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
                    for (var entry = 0; entry < count; entry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var instance = manager.LoadAssetsFileFromBundle(bundle, entry, false);
                        if (instance?.file is null) continue;
                        extracted += WriteShaderOnlyCopy(instance.file, output, index * 1000 + entry, baseName, records, beforeHash, relative);
                    }
                }
                finally { manager.UnloadAllAssetsFiles(true); manager.UnloadAllBundleFiles(); }
            }
            if (!HashFile(path).Equals(beforeHash, StringComparison.Ordinal))
                throw new IOException("Original game asset changed during read-only shader extraction: " + relative);
            totalShaders += extracted;
            index++;
        }
        var manifest = new { schemaVersion = 1, gameDataPath = gameData, inputFiles = records.Count, shaderObjects = totalShaders, files = records };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return (records.Count, totalShaders);
    }

    private static int WriteShaderOnlyCopy(AssetsFile assets, string output, int ordinal, string baseName,
        List<object> records, string sourceHash, string sourcePath)
    {
        var shaderInfos = assets.AssetInfos.Where(info => info.GetTypeId(assets) == (int)AssetClassID.Shader).ToArray();
        if (shaderInfos.Length == 0) return 0;
        var targetName = $"{ordinal:D6}-{baseName}.assets";
        foreach (var info in assets.AssetInfos.ToArray())
            if (info.GetTypeId(assets) != (int)AssetClassID.Shader) info.SetRemoved();
        using (var outputStream = File.Create(Path.Combine(output, targetName)))
        using (var writer = new AssetsFileWriter(outputStream)) assets.Write(writer, 0);
        records.Add(new { source = sourcePath, copy = targetName, shaderCount = shaderInfos.Length, sha256 = sourceHash });
        return shaderInfos.Length;
    }

    private static bool IsUnityFs(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[7];
        return stream.Read(signature) == signature.Length && signature.SequenceEqual("UnityFS"u8);
    }

    private static bool IsSourceShader(string path)
    {
        var text = File.ReadAllText(path);
        return text.Contains("Shader \"", StringComparison.Ordinal) &&
               !text.Contains("Shader \"Hidden/AssetRipper", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("Generated by AssetRipper", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("Shader \"Hidden/InternalErrorShader", StringComparison.Ordinal);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static async Task WaitForService(Uri service, Process? server, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(45);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server?.HasExited == true) throw new InvalidOperationException("AssetRipper exited before its local service became ready.");
            try { using var response = await client.GetAsync(service, cancellationToken); if ((int)response.StatusCode < 500) return; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { last = e; }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException("AssetRipper local service did not become ready." + (last is null ? "" : " " + last.Message));
    }

    private static async Task RequireAssetRipperVersion(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not query AssetRipper version.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var version = (await stdout) + " " + (await stderr);
        if (process.ExitCode != 0 || !version.Contains("1.0.15", StringComparison.Ordinal))
            throw new InvalidDataException("Shader source recovery requires AssetRipper 1.0.15; reported version: " + version.Trim());
    }

    private static async Task PostForm(Uri baseUri, string endpoint, string form, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await client.PostAsync(new Uri(baseUri, endpoint), content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static bool IsWithin(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
