using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace H3VR.Converter;

/// <summary>Bootstraps the pinned AssetRipper source worker without a service or game-specific API.</summary>
public static class AssetRipperIntegration
{
    public const string UpstreamCommit = "1ac666f47d8e9dedf96afb0b914c70d7656151ea";
    public const string SubmodulePath = "extern/AssetRipper";
    public const string UpstreamUrl = "https://github.com/AssetRipper/AssetRipper.git";
    private const int CacheSchema = 1;
    private const string WorkerDll = "AssetRipper.Worker.dll";

    public static string DefaultCacheDirectory(ConversionConfig config) => Path.Combine(
        config.ToolCacheDirectory ?? InstallationDiscovery.DefaultToolCache(), "assetripper2");

    /// <summary>Read-only check; does not initialize the submodule or create cache files.</summary>
    public static bool CheckAvailability(ConversionConfig config, string repository, out string detail)
    {
        var fingerprint = Fingerprint(repository);
        var cache = Path.Combine(DefaultCacheDirectory(config), fingerprint);
        if (TryVerifyCache(cache, fingerprint, out _))
        {
            detail = "Verified AssetRipper worker cache is available.";
            return true;
        }
        if (config.Offline)
        {
            detail = "Offline mode requires a previously built, verified AssetRipper worker cache at " + cache;
            return false;
        }
        try
        {
            ValidateSubmoduleMetadata(repository);
            var source = Path.Combine(repository, SubmodulePath);
            if (File.Exists(Path.Combine(source, ".git")) || Directory.Exists(Path.Combine(source, ".git")))
            {
                var head = ReadProcess("git", ["-C", source, "rev-parse", "HEAD"], repository).Trim();
                if (!head.Equals(UpstreamCommit, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"AssetRipper source is at {head}, expected pinned commit {UpstreamCommit}.";
                    return false;
                }
                var status = ReadProcess("git", ["-C", source, "status", "--porcelain", "--untracked-files=all"], repository);
                if (!string.IsNullOrWhiteSpace(status))
                {
                    detail = "AssetRipper source has local changes; resolve them before extraction.";
                    return false;
                }
                detail = "Pinned AssetRipper source is available; the .NET SDK is required on first extraction.";
                return true;
            }
            detail = "Pinned AssetRipper source and .NET SDK will be required on first extraction.";
            return true;
        }
        catch (Exception error)
        {
            detail = error.Message;
            return false;
        }
    }

    public static async Task ExportAsync(string repository, ConversionConfig config, string outputDirectory,
        string logPath, CancellationToken cancellation)
    {
        repository = Path.GetFullPath(repository);
        var fingerprint = Fingerprint(repository);
        var cacheRoot = DefaultCacheDirectory(config);
        var cache = Path.Combine(cacheRoot, fingerprint);

        var executable = await GetWorkerAsync(repository, config, cache, fingerprint, cancellation);
        cancellation.ThrowIfCancellationRequested();
        Console.WriteLine("Exporting with AssetRipper. Progress log: " + logPath);
        await RunWorkerAsync(executable, config.GameDirectory, outputDirectory, repository, logPath, cancellation);
    }

    private static async Task<string> GetWorkerAsync(string repository, ConversionConfig config, string cache,
        string fingerprint, CancellationToken cancellation)
    {
        if (TryVerifyCache(cache, fingerprint, out var verifiedWorker)) return verifiedWorker;
        if (config.Offline)
            throw new InvalidOperationException("Offline mode has no verified AssetRipper worker cache. Run extraction once online, or disable offline mode. Expected cache: " + cache);

        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        var leasePath = cache + ".lock";
        using var lease = AcquireLease(leasePath);
        if (TryVerifyCache(cache, fingerprint, out verifiedWorker)) return verifiedWorker;
        if (Directory.Exists(cache))
            throw new InvalidDataException("AssetRipper worker cache exists but failed its integrity check; move it aside and retry: " + cache);
        ValidateSubmoduleMetadata(repository);
        await EnsurePinnedSubmoduleAsync(repository, cancellation);

        var stage = cache + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            var publish = Path.Combine(stage, "publish");
            Directory.CreateDirectory(publish);
            var project = Path.Combine(repository, "tools", "AssetRipper.Worker", "AssetRipper.Worker.csproj");
            await RunProcessAsync("dotnet", ["publish", project, "--configuration", "Release", "--runtime", RuntimeInformation.RuntimeIdentifier,
                    "--output", publish, "--nologo"],
                repository, null, cancellation);
            if (!File.Exists(Path.Combine(publish, WorkerDll)))
                throw new InvalidDataException("AssetRipper worker publish completed without " + WorkerDll);
            var manifest = CreateManifest(publish, fingerprint);
            await File.WriteAllTextAsync(Path.Combine(publish, "worker-manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), cancellation);
            Directory.Move(publish, cache);
            return Path.Combine(cache, WorkerDll);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record CacheManifest(int Schema, string Fingerprint, string UpstreamCommit, int RuntimeMajor,
        SortedDictionary<string, string> Files);

    private static CacheManifest CreateManifest(string directory, string fingerprint)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in SafeFiles.Files(directory))
        {
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(relative, HashFile(file));
        }
        return new(CacheSchema, fingerprint, UpstreamCommit, Environment.Version.Major, files);
    }

    private static bool TryVerifyCache(string directory, string fingerprint, out string worker)
    {
        worker = Path.Combine(directory, WorkerDll);
        try
        {
            var cacheRoot = new DirectoryInfo(directory);
            var manifestPath = Path.Combine(directory, "worker-manifest.json");
            if (!Directory.Exists(directory) || cacheRoot.LinkTarget is not null || !File.Exists(manifestPath) ||
                new FileInfo(manifestPath).LinkTarget is not null) return false;
            var manifest = JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.Schema != CacheSchema || manifest.Fingerprint != fingerprint ||
                manifest.UpstreamCommit != UpstreamCommit || manifest.RuntimeMajor != Environment.Version.Major ||
                manifest.Files is null || !manifest.Files.ContainsKey(WorkerDll)) return false;
            var actualFiles = SafeFiles.Files(directory)
                .Where(path => !Path.GetFileName(path).Equals("worker-manifest.json", StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.Ordinal);
            if (!actualFiles.SetEquals(manifest.Files.Keys)) return false;
            foreach (var (relative, expectedHash) in manifest.Files)
            {
                var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(path, directory) || !File.Exists(path) || !HashFile(path).Equals(expectedHash, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string Fingerprint(string repository)
    {
        var paths = new[]
        {
            "tools/AssetRipper.Worker/AssetRipper.Worker.csproj",
            "tools/AssetRipper.Worker/Program.cs",
            "Directory.Build.props",
            "global.json"
        };
        var text = new StringBuilder($"schema={CacheSchema}\ncommit={UpstreamCommit}\nruntime={Environment.Version.Major}\nrid={RuntimeInformation.RuntimeIdentifier}\n");
        foreach (var relative in paths)
        {
            var file = Path.Combine(repository, relative);
            text.Append(relative).Append('=').Append(File.Exists(file) ? HashFile(file) : "missing").Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string HashFile(string path) => SafeFiles.Hash(path);

    private static void ValidateSubmoduleMetadata(string repository)
    {
        var gitmodules = Path.Combine(repository, ".gitmodules");
        if (!File.Exists(gitmodules)) throw new InvalidDataException("The checkout is missing .gitmodules for the pinned AssetRipper source.");
        if (!Publication.ApprovedModules(File.ReadAllText(gitmodules)))
            throw new InvalidDataException($"Expected {SubmodulePath} to use the pinned public source {UpstreamUrl}; refusing altered submodule metadata.");
    }

    private static async Task EnsurePinnedSubmoduleAsync(string repository, CancellationToken cancellation)
    {
        var path = Path.Combine(repository, SubmodulePath);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, ".git")))
            await RunProcessAsync("git", ["-c", "protocol.file.allow=never", "-c",
                "submodule.extern/AssetRipper.url=" + UpstreamUrl,
                "submodule", "update", "--init", "--depth", "1", "--", SubmodulePath], repository, null, cancellation);
        var head = await CaptureProcessAsync("git", ["-C", path, "rev-parse", "HEAD"], repository, cancellation);
        if (!head.Trim().Equals(UpstreamCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"AssetRipper submodule is at {head.Trim()}, expected {UpstreamCommit}. Update the checkout to the pinned commit; it will not be reset automatically.");
        var status = await CaptureProcessAsync("git", ["-C", path, "status", "--porcelain", "--untracked-files=all"], repository, cancellation);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidDataException("AssetRipper submodule has local changes. Preserve or remove them yourself before extraction; the converter will not reset it.");
    }

    private static FileStream AcquireLease(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("Another AssetRipper worker bootstrap is active (or its cache lock is held): " + path, error); }
    }

    private static async Task RunWorkerAsync(string workerDll, string gameDirectory, string outputDirectory,
        string repository, string logPath, CancellationToken cancellation)
    {
        SafeFiles.RequireSeparate(gameDirectory, outputDirectory);
        SafeFiles.RequireSeparate(repository, outputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("AssetRipper output directory must be empty; refusing to overwrite existing files: " + output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await RunProcessAsync("dotnet", [workerDll, Path.GetFullPath(gameDirectory), output], repository, logPath, cancellation);
    }

    private static bool IsWithin(string path, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static async Task<string> CaptureProcessAsync(string file, IEnumerable<string> args, string cwd, CancellationToken cancellation)
    {
        var output = new StringBuilder();
        await RunProcessAsync(file, args, cwd, null, cancellation, output);
        return output.ToString();
    }

    private static string ReadProcess(string file, IEnumerable<string> args, string cwd)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + file);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} exited with code {process.ExitCode}: {stderr}");
        return stdout;
    }

    private static async Task RunProcessAsync(string file, IEnumerable<string> args, string cwd, string? log,
        CancellationToken cancellation, StringBuilder? capture = null)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + file);
        using var writer = log is null ? null : new StreamWriter(log, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        using var sync = new SemaphoreSlim(1, 1);
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                await sync.WaitAsync(cancellation);
                try
                {
                    capture?.AppendLine(line);
                    if (writer is not null) await writer.WriteLineAsync(line);
                    else if (capture is null) Console.WriteLine(line);
                }
                finally { sync.Release(); }
            }
        }
        var stdout = Pump(process.StandardOutput);
        var stderr = Pump(process.StandardError);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited with code {process.ExitCode}. See {log ?? "the output above"}.");
    }
}
