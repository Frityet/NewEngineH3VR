using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
    string AssetRipperExecutable);

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
        SafeFiles.RequireSeparate(gameData, output);
        RejectSymlinks(gameData);
        if (Directory.Exists(output)) RejectSymlinks(output);
        var serviceUri = ReserveEphemeralLoopbackUri();
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

        await RunExportService(ripper, serviceUri, inputs, export, output, cancellationToken);

        // The upstream web middleware can return HTTP 200 after logging an
        // exception, leaving a partial export that otherwise looks successful.
        var logPath = Path.Combine(output, "assetripper.log");
        if (ExportLogHasException(File.ReadAllText(logPath)))
            throw new InvalidDataException("AssetRipper logged an exception and may have produced a partial export. Inspect " + logPath);

        var projectAssets = Path.Combine(export, "ExportedProject", "Assets");
        var shaderFiles = Directory.Exists(projectAssets)
            ? Directory.EnumerateFiles(projectAssets, "*.shader", SearchOption.AllDirectories).ToArray()
            : [];
        var accepted = shaderFiles.Where(IsSourceShader).ToArray();
        if (accepted.Length == 0) throw new InvalidDataException("AssetRipper produced no non-dummy .shader source files.");
        var trace = Path.Combine(output, "shader-export-rows.jsonl");
        var identityPath = Path.Combine(output, "shader-export-identities.json");
        if (!File.Exists(trace))
            throw new InvalidDataException("The local shader exporter did not emit source identities.");
        VerifyExportIdentities(inventory.RootElement, trace, Path.Combine(export, "ExportedProject"), accepted, identityPath);
        var toolManifest = Path.Combine(Path.GetDirectoryName(ripper)!, "h3vr-shader-tool-cache.json");
        var fileHashes = shaderFiles.OrderBy(p => p, StringComparer.Ordinal).ToDictionary(
            p => Path.GetRelativePath(export, p).Replace('\\', '/'), HashFile, StringComparer.Ordinal);

        var receipt = new
        {
            schemaVersion = 2,
            exporter = "AssetRipper 1.0.15-compatible local build",
            generatedUtc = DateTimeOffset.UtcNow,
            gameDataPath = gameData,
            shaderObjects,
            exportedShaderFiles = shaderFiles.Length,
            usableSourceFiles = accepted.Length,
            validationState = "raw-export-unvalidated",
            limitation = "No Unity compilation, original-program equivalence, variant coverage, Metal pipeline, or visual validation is implied.",
            inputManifest = Path.GetFileName(inventoryPath),
            inputManifestSha256 = HashFile(inventoryPath),
            exportIdentityManifest = Path.GetFileName(identityPath),
            exportIdentityManifestSha256 = HashFile(identityPath),
            exporterExecutableSha256 = HashFile(ripper),
            exporterToolManifestSha256 = File.Exists(toolManifest) ? HashFile(toolManifest) : null,
            exporterToolManifest = File.Exists(toolManifest) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(toolManifest)) : (JsonElement?)null,
            exportedFilesSha256 = fileHashes,
            sha256 = accepted.OrderBy(p => p, StringComparer.Ordinal).ToDictionary(
                p => Path.GetRelativePath(export, p).Replace('\\', '/'),
                p => fileHashes[Path.GetRelativePath(export, p).Replace('\\', '/')],
                StringComparer.Ordinal)
        };
        await File.WriteAllTextAsync(Path.Combine(output, "recovery-receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return new ShaderRecoveryResult(output, inputs, inputFiles, shaderObjects, accepted.Length, "raw-export-unvalidated");
    }

    internal static bool ExportLogHasException(string log) =>
        System.Text.RegularExpressions.Regex.IsMatch(log, @"(?m)^\[std(?:out|err)\] (?:System\.)?\S*Exception(?::|\s)");

    private sealed record ExportRow(string Collection, long PathId, string Guid, string Path);

    internal static void VerifyExportIdentities(JsonElement inventory, string trace, string project, string[] accepted, string receipt)
    {
        var inputs = inventory.GetProperty("files").EnumerateArray()
            .ToDictionary(row => row.GetProperty("copy").GetString()!, StringComparer.OrdinalIgnoreCase);
        var exported = new HashSet<string>(StringComparer.Ordinal);
        var usedObjects = new HashSet<(string Copy, long PathId)>();
        var usedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<object>();
        foreach (var line in File.ReadLines(trace))
        {
            var row = JsonSerializer.Deserialize<ExportRow>(line, ConversionConfig.Json)
                ?? throw new InvalidDataException("The shader exporter emitted an empty identity row.");
            if (!inputs.TryGetValue(row.Collection, out var input) ||
                !input.GetProperty("pathIds").EnumerateArray().Any(id => id.GetInt64() == row.PathId))
                throw new InvalidDataException("Raw export does not resolve to a shader-only input object: " + row.Collection);
            string path = SafeFiles.Inside(project, row.Path);
            if (!File.Exists(path) || !File.Exists(path + ".meta") || new FileInfo(path).LinkTarget is not null ||
                !exported.Add(path) || !usedObjects.Add((input.GetProperty("copy").GetString()!, row.PathId)) ||
                !usedGuids.Add(row.Guid))
                throw new InvalidDataException("Missing or ambiguous raw shader export identity: " + row.Path);
            var meta = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path + ".meta"), @"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$");
            if (!meta.Success || !meta.Groups[1].Value.Equals(row.Guid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The raw shader export GUID differs from its metadata: " + row.Path);
            bindings.Add(new
            {
                source = input.GetProperty("source").GetString(),
                sourceSha256 = input.GetProperty("sha256").GetString(),
                bundleEntry = input.GetProperty("bundleEntry"),
                serializedName = input.GetProperty("serializedName").GetString(),
                row.PathId,
                rawGuid = row.Guid,
                rawPath = row.Path,
            });
        }
        if (!accepted.All(exported.Contains))
            throw new InvalidDataException("Some raw shader files lack an exact original source identity.");
        SafeFiles.AtomicJson(receipt, new { schemaVersion = 1, validationState = "raw-export-unvalidated", bindings });
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
                extracted = WriteShaderOnlyCopy(assets, output, index, baseName, records, beforeHash, relative, Path.GetFileName(path), null);
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
                        var serialized = bundle.file.BlockAndDirInfo.DirectoryInfos[entry].Name;
                        extracted += WriteShaderOnlyCopy(instance.file, output, index * 1000 + entry, baseName, records, beforeHash, relative, serialized, entry);
                    }
                }
                finally { manager.UnloadAllAssetsFiles(true); manager.UnloadAllBundleFiles(); }
            }
            if (!HashFile(path).Equals(beforeHash, StringComparison.Ordinal))
                throw new IOException("Original game asset changed during read-only shader extraction: " + relative);
            totalShaders += extracted;
            index++;
        }
        var manifest = new { schemaVersion = 3, gameDataPath = gameData, inputFiles = records.Count, shaderObjects = totalShaders, files = records };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return (records.Count, totalShaders);
    }

    private static int WriteShaderOnlyCopy(AssetsFile assets, string output, int ordinal, string baseName,
        List<object> records, string sourceHash, string sourcePath, string serializedName, int? bundleEntry)
    {
        var shaderInfos = assets.AssetInfos.Where(info => info.GetTypeId(assets) == (int)AssetClassID.Shader).ToArray();
        if (shaderInfos.Length == 0) return 0;
        var targetName = $"{ordinal:D6}-{baseName}.assets";
        var pathIds = ShaderPathIds(shaderInfos.Select(info => info.PathId), sourcePath);
        foreach (var info in assets.AssetInfos.ToArray())
            if (info.GetTypeId(assets) != (int)AssetClassID.Shader) info.SetRemoved();
        var targetPath = Path.Combine(output, targetName);
        using (var outputStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new AssetsFileWriter(outputStream)) assets.Write(writer, 0);
        records.Add(new { source = sourcePath, serializedName, bundleEntry, copy = targetName, shaderCount = shaderInfos.Length, pathIds, sha256 = sourceHash, copySha256 = HashFile(targetPath) });
        return shaderInfos.Length;
    }

    internal static long[] ShaderPathIds(IEnumerable<long> ids, string source)
    {
        var sorted = ids.Order().ToArray();
        if (sorted.Distinct().Count() != sorted.Length) throw new InvalidDataException("Duplicate shader path IDs in " + source);
        return sorted;
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

    private static async Task RunExportService(string executable, Uri service, string inputs, string export,
        string output, CancellationToken cancellationToken)
    {
        Process? server = null;
        StreamWriter? log = null;
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var logGate = new object();
        try
        {
            var logPath = Path.Combine(output, "assetripper.log");
            log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = output,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--port"); start.ArgumentList.Add(service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--launch-browser"); start.ArgumentList.Add("false");
            start.Environment["NATIVEH3VR_SHADER_EXPORT_TRACE"] = Path.Combine(output, "shader-export-rows.jsonl");
            server = Process.Start(start) ?? throw new InvalidOperationException("Could not start AssetRipper.");
            stdout = PumpLog(server.StandardOutput, "stdout", log, logGate);
            stderr = PumpLog(server.StandardError, "stderr", log, logGate);

            await WaitForService(service, server, cancellationToken);
            EnsureProcessAlive(server, "before export settings");
            using var settingsDocument = JsonDocument.Parse(AssetRipperSettings);
            var settingsForm = string.Join("&", settingsDocument.RootElement.EnumerateObject()
                .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.ToString())));
            await PostForm(service, "Settings/Update", settingsForm, cancellationToken);
            EnsureProcessAlive(server, "after export settings");

            var assets = Directory.EnumerateFiles(inputs, "*.assets", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var loadForm = string.Join("&", assets.Select(p => "Path=" + Uri.EscapeDataString(p)));
            await PostForm(service, "LoadFile", loadForm, cancellationToken);
            EnsureProcessAlive(server, "after asset loading");
            await PostForm(service, "Export", "Path=" + Uri.EscapeDataString(export), cancellationToken);
            EnsureProcessAlive(server, "after export");
        }
        finally
        {
            if (server is not null)
            {
                await KillAndDrain(server, stdout, stderr);
                server.Dispose();
                await Task.WhenAll(stdout, stderr);
            }
            if (log is not null) await log.DisposeAsync();
        }
    }

    private static async Task RequireAssetRipperVersion(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not query AssetRipper version.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        string version;
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(linked.Token));
            version = (await stdout) + " " + (await stderr);
        }
        catch (OperationCanceledException)
        {
            await KillAndDrain(process, stdout, stderr);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("AssetRipper --version did not complete within 30 seconds.");
        }
        catch
        {
            await KillAndDrain(process, stdout, stderr);
            throw;
        }
        if (process.ExitCode != 0 || !version.Contains("1.0.15", StringComparison.Ordinal))
            throw new InvalidDataException("Shader source recovery requires AssetRipper 1.0.15; reported version: " + version.Trim());
    }

    private static async Task PostForm(Uri baseUri, string endpoint, string form, CancellationToken cancellationToken)
    {
        // Large serialized shaders can take well over ten minutes to decompile.
        // Keep a bounded timeout while preserving Ctrl-C cancellation of the owned child.
        using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        try
        {
            using var response = await client.PostAsync(new Uri(baseUri, endpoint), content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("AssetRipper " + endpoint + " did not complete within two hours; inspect the exporter log.");
        }
    }

    private static async Task WaitForService(Uri service, Process? server, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureProcessAlive(server, "while waiting for the local service");
            try
            {
                using var response = await client.GetAsync(service, cancellationToken);
                EnsureProcessAlive(server, "when the local service responded");
                if ((int)response.StatusCode < 500) return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                last = e;
            }
            await Task.Delay(300, cancellationToken);
        }
        throw new TimeoutException("AssetRipper local service did not become ready within 60 seconds." + (last is null ? "" : " " + last.Message));
    }

    private static void EnsureProcessAlive(Process? process, string phase)
    {
        if (process?.HasExited == true)
            throw new InvalidOperationException($"The owned AssetRipper process exited {phase} (code {process.ExitCode}). Inspect assetripper.log.");
    }

    private static async Task PumpLog(StreamReader reader, string streamName, StreamWriter log, object gate)
    {
        while (await reader.ReadLineAsync() is { } line)
            lock (gate) log.WriteLine($"[{streamName}] {line}");
    }

    private static async Task KillAndDrain(Process process, Task stdout, Task stderr)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync(CancellationToken.None); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await Task.WhenAll(stdout, stderr); } catch (Exception) { }
    }

    private static Uri ReserveEphemeralLoopbackUri()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), port, "/").Uri;
    }

    private static void RejectSymlinks(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null) throw new InvalidDataException("Shader recovery does not accept symlinked input/output children: " + entry.FullName);
                if (entry is DirectoryInfo directory) pending.Push(directory);
            }
        }
    }

}
