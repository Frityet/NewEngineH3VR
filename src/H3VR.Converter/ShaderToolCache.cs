using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace H3VR.Converter;

public sealed class ShaderToolCacheManifest
{
    public int schemaVersion = 2;
    public string upstreamCommit = "";
    public string forkRepository = "";
    public string forkCommit = "";
    public string runtimeIdentifier = "";
    public string executable = "";
    public Dictionary<string, string> files = new(StringComparer.Ordinal);
}

/// <summary>Content-addressed, integrity-checked per-user cache for the pinned fork exporter.</summary>
public static class ShaderToolCache
{
    private const string ManifestName = "h3vr-shader-tool-cache.json";
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public static Task<string> EnsureAsync(string cacheDirectory, bool offline, CancellationToken cancellation) =>
        EnsureAsync(Repository.Find(), cacheDirectory, offline, cancellation);

    internal static async Task<string> EnsureAsync(string repository, string cacheDirectory, bool offline, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        var root = Path.GetFullPath(cacheDirectory);
        var commit = ShaderExporter.UpstreamCommit;
        var rid = CurrentRuntimeIdentifier();
        // A publish can succeed while omitting runtime dependencies.
        var key = $"{ShaderExporter.ForkCommit}-{rid}";
        var destination = Path.Combine(root, key);
        var locks = Path.Combine(root, ".locks");
        var lockPath = Path.Combine(locks, key + ".lock");
        SafeFiles.RequireToolCachePath(repository, destination);
        SafeFiles.RequireToolCachePath(repository, lockPath);
        Directory.CreateDirectory(locks);
        await using var lease = await AcquireLock(lockPath, cancellation);

        if (Directory.Exists(destination))
        {
            if (TryValidate(destination, commit, rid, out var existing, out var reason)) return existing;
            throw new InvalidDataException($"Cached ShaderExporter is invalid at '{destination}': {reason} Preserve or inspect that directory, then choose a fresh tool cache path; it was not deleted or replaced.");
        }
        if (File.Exists(destination))
            throw new InvalidDataException($"ShaderExporter cache path is occupied by a file: {destination}. Choose a fresh tool cache path.");
        if (offline)
            throw new InvalidOperationException($"ShaderExporter is not cached for {rid}. Run once online to provision it, or choose a cache path containing the verified exporter: {destination}");

        var staging = Path.Combine(root, ".shader-exporter-provision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executable = await ShaderExporter.ProvisionAsync(repository, staging, cancellation);
            if (!TryCreateManifest(staging, commit, rid, executable, out var manifest, out var reason))
                throw new InvalidDataException("Provisioned ShaderExporter failed integrity checks: " + reason);
            await File.WriteAllTextAsync(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, Json) + "\n", cancellation);
            if (!TryValidate(staging, commit, rid, out _, out reason))
                throw new InvalidDataException("Provisioned ShaderExporter cache failed its post-write verification: " + reason);
            Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        if (!TryValidate(destination, commit, rid, out var finalExecutable, out var finalReason))
            throw new InvalidDataException($"Published ShaderExporter cache failed integrity verification at '{destination}': {finalReason}");
        return finalExecutable;
    }

    internal static string CurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 when OperatingSystem.IsWindows() => "x86",
            _ => throw new PlatformNotSupportedException($"Unsupported AssetRipper process architecture: {RuntimeInformation.ProcessArchitecture}.")
        };
        var platform = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("AssetRipper provisioning supports macOS, Windows, and Linux.");
        return $"{platform}-{architecture}";
    }

    internal static bool TryValidate(string directory, string commit, string rid, out string executable, out string reason)
    {
        executable = "";
        reason = "";
        try
        {
            if (!Directory.Exists(directory)) { reason = "cache directory is missing"; return false; }
            var manifestPath = Path.Combine(directory, ManifestName);
            if (!File.Exists(manifestPath)) { reason = "integrity manifest is missing"; return false; }
            var manifest = JsonSerializer.Deserialize<ShaderToolCacheManifest>(File.ReadAllText(manifestPath), Json)
                ?? throw new InvalidDataException("manifest is empty");
            if (manifest.schemaVersion != 2 || manifest.upstreamCommit != commit || manifest.forkRepository != ShaderExporter.ForkRepository || manifest.forkCommit != ShaderExporter.ForkCommit || manifest.runtimeIdentifier != rid)
            { reason = "manifest provenance does not match the pinned fork, upstream and host runtime"; return false; }
            if (manifest.files.Count == 0) { reason = "manifest contains no output hashes"; return false; }
            executable = SafeFiles.Inside(directory, manifest.executable);
            if (!File.Exists(executable)) { reason = "manifest executable is missing"; return false; }

            var actual = SafeFiles.Files(directory).Where(p => !Path.GetFileName(p).Equals(ManifestName, StringComparison.Ordinal))
                .ToDictionary(p => Path.GetRelativePath(directory, p).Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal);
            if (actual.Count != manifest.files.Count || actual.Keys.Except(manifest.files.Keys, StringComparer.Ordinal).Any() ||
                manifest.files.Keys.Except(actual.Keys, StringComparer.Ordinal).Any())
            { reason = "cached file inventory differs from the integrity manifest"; return false; }
            foreach (var (relative, expected) in manifest.files)
            {
                var file = SafeFiles.Inside(directory, relative);
                if (!SafeFiles.Hash(file).Equals(expected, StringComparison.Ordinal))
                { reason = "cached file hash mismatch: " + relative; return false; }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            executable = "";
            reason = e.Message;
            return false;
        }
    }

    internal static bool TryCreateManifest(string directory, string commit, string rid, string executablePath,
        out ShaderToolCacheManifest manifest, out string reason)
    {
        manifest = new ShaderToolCacheManifest();
        reason = "";
        try
        {
            if (!SafeFiles.Contains(directory, executablePath)) throw new InvalidDataException("provisioned executable is outside the cache directory");
            var relativeExecutable = Path.GetRelativePath(directory, executablePath).Replace(Path.DirectorySeparatorChar, '/');
            var files = SafeFiles.Files(directory).ToArray();
            if (files.Length == 0 || !files.Contains(Path.GetFullPath(executablePath), PathComparer()))
                throw new InvalidDataException("provisioner produced no executable files");
            manifest = new ShaderToolCacheManifest
            {
                upstreamCommit = commit,
                forkRepository = ShaderExporter.ForkRepository,
                forkCommit = ShaderExporter.ForkCommit,
                runtimeIdentifier = rid,
                executable = relativeExecutable,
                files = files.ToDictionary(p => Path.GetRelativePath(directory, p).Replace(Path.DirectorySeparatorChar, '/'), SafeFiles.Hash, StringComparer.Ordinal)
            };
            if (!manifest.files.Keys.Any(k => k.Equals("AssetRipper-SOURCE.md", StringComparison.Ordinal)) ||
                !manifest.files.Keys.Any(k => k.Equals("AssetRipper-LICENSE.md", StringComparison.Ordinal)))
                throw new InvalidDataException("provisioner omitted required source and license provenance files");
            var sourceNotice = File.ReadAllText(Path.Combine(directory, "AssetRipper-SOURCE.md"));
            if (!sourceNotice.Contains(commit, StringComparison.Ordinal) || !sourceNotice.Contains(ShaderExporter.ForkRepository, StringComparison.Ordinal) || !sourceNotice.Contains(ShaderExporter.ForkCommit, StringComparison.Ordinal) || !sourceNotice.Contains("1.0.15", StringComparison.Ordinal))
                throw new InvalidDataException("provisioned source notice does not identify the pinned AssetRipper commit and version");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            reason = e.Message;
            return false;
        }
    }

    private static async Task<FileStream> AcquireLock(string path, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(300, cancellation); }
        }
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
