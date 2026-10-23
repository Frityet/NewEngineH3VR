using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

public static class Integration
{
    private sealed record FileEntry(string Source, string Destination, string Sha256);
    private sealed record Manifest(int SchemaVersion, string UnityVersion, string BuildTarget, string XrBackend, string? Provider, List<FileEntry> Files);

    /// <summary>Apply explicitly supplied, hash-pinned OpenXR provider files to a project candidate.</summary>
    public static void Apply(string manifestPath, string candidate, string buildTarget)
    {
        var fullManifest = Path.GetFullPath(manifestPath); if (!File.Exists(fullManifest)) throw new FileNotFoundException("Integration manifest not found.", fullManifest);
        var sourceRoot = Path.GetDirectoryName(fullManifest)!;
        if (new FileInfo(fullManifest).LinkTarget is not null) throw new InvalidDataException("Integration manifest symlinks are not supported.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(fullManifest), ConversionConfig.Json)
            ?? throw new InvalidDataException("Integration manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.UnityVersion != ConversionConfig.TargetVersion || manifest.BuildTarget != buildTarget || manifest.XrBackend != "OpenXR")
            throw new InvalidDataException("Integration manifest must target schema 1, Unity " + ConversionConfig.TargetVersion + ", build target " + buildTarget + ", and OpenXR.");
        var hasProvider = !string.IsNullOrWhiteSpace(manifest.Provider);
        if (buildTarget != "StandaloneWindows64" && !hasProvider)
            throw new InvalidDataException("Non-Windows OpenXR builds require Provider to name a managed XR loader class.");
        if (hasProvider && !Regex.IsMatch(manifest.Provider!, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
            throw new InvalidDataException("Provider must be a fully qualified managed XR loader type name.");
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("Integration manifest must list at least one hash-pinned provider file.");
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Destination) || !System.Text.RegularExpressions.Regex.IsMatch(entry.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Each integration file requires source, destination, and a SHA-256 hash.");
            var source = SafeFiles.Inside(sourceRoot, entry.Source);
            if (!File.Exists(source)) throw new FileNotFoundException("Integration source file not found.", source);
            if (new FileInfo(source).LinkTarget is not null) throw new InvalidDataException("Integration source symlinks are not supported: " + entry.Source);
            var actual = SafeFiles.Hash(source);
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Integration source hash mismatch: " + entry.Source);
            var destRelative = entry.Destination.Replace('\\', '/');
            if (!(destRelative.StartsWith("Assets/Plugins/", StringComparison.Ordinal) || destRelative.StartsWith("Assets/OpenXRProvider/", StringComparison.Ordinal)))
                throw new InvalidDataException("Integration destinations are limited to Assets/Plugins or Assets/OpenXRProvider: " + entry.Destination);
            var destination = SafeFiles.Inside(candidate, destRelative);
            if (!seen.Add(destination)) throw new InvalidDataException("Duplicate integration destination: " + destRelative);
            var cursor = destination;
            while (SafeFiles.Contains(candidate, cursor) && cursor != Path.GetFullPath(candidate))
            {
                FileSystemInfo item = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);
                if (item.Exists && item.LinkTarget is not null) throw new InvalidDataException("Integration destination passes through a symlink: " + cursor);
                cursor = Path.GetDirectoryName(cursor)!;
            }
            if (File.Exists(destination))
            {
                if (!SafeFiles.Hash(destination).Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new IOException("Integration refuses to overwrite a different existing file: " + destRelative);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, false);
        }
        if (hasProvider)
        {
            var settings = SafeFiles.Inside(candidate, "ProjectSettings/H3VRProvider.json");
            var content = JsonSerializer.Serialize(new { loader = manifest.Provider }, ConversionConfig.Json) + "\n";
            if (File.Exists(settings) && File.ReadAllText(settings) != content) throw new IOException("Candidate already contains different H3VR provider configuration.");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            if (!File.Exists(settings)) File.WriteAllText(settings, content);
        }
    }
}
