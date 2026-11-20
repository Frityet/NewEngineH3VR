using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

public static class Integration
{
    internal const string MetalBridge = "NativeH3VR.UnityMetal";
    private const string MetalRendererSource = "Assets/OpenXRProvider/NativeStereoRenderer.cs";
    private static readonly string[] MetalSources =
    [
        "Assets/OpenXRProvider/MetalOpenXR.cs",
        MetalRendererSource,
        "Assets/OpenXRProvider/H3VRMetalBinding.cs",
        "Assets/OpenXRProvider/SyntheticHands.cs",
        "Assets/OpenXRProvider/StockGearNormals.cs",
    ];
    private static readonly string[] MetalLibraries =
    [
        "Assets/Plugins/macOS/libNativeH3VRMetal.dylib",
        "Assets/Plugins/Native/macOS/libsteam_api.dylib",
        "Assets/Plugins/Native/macOS/libCSteamworks.dylib",
        "Assets/Plugins/Native/macOS/libfmod.dylib",
        "Assets/Plugins/Native/macOS/libfmodstudio.dylib",
        "Assets/Plugins/Native/macOS/libNativeH3VRFMOD.dylib",
    ];
    internal static IEnumerable<string> RequiredMetalFiles() =>
        MetalSources.Concat(MetalLibraries).Concat(MetalLibraries.Select(file => file + ".meta"));

    internal static void PlaceMacPlayerLibraries(string app)
    {
        var plugins = Path.Combine(app, "Contents", "PlugIns");
        var architectureDirectory = Path.Combine(plugins, "ARM64");
        var moves = new List<(string Source, string Destination)>();
        foreach (var name in MetalLibraries.Select(Path.GetFileName))
        {
            var direct = Path.Combine(plugins, name!);
            var nested = Path.Combine(architectureDirectory, name!);
            RejectLinks(app, direct);
            RejectLinks(app, nested);
            if (Directory.Exists(direct) || Directory.Exists(nested))
                throw new InvalidDataException("A macOS player library path is a directory: " + name);
            if (File.Exists(direct))
            {
                if (File.Exists(nested))
                    throw new InvalidDataException("Duplicate macOS player library: " + name);
                continue;
            }
            if (!File.Exists(nested))
                throw new FileNotFoundException("Missing macOS player library: " + name, nested);
            moves.Add((nested, direct));
        }

        // Mono's Unity player plug-in resolver searches PlugIns directly for
        // the old Steamworks and FMOD imports. Their dependencies share it.
        foreach (var move in moves) File.Move(move.Source, move.Destination);
        if (Directory.Exists(architectureDirectory) && !Directory.EnumerateFileSystemEntries(architectureDirectory).Any())
            Directory.Delete(architectureDirectory);
    }

    private sealed record FileEntry(string Source, string Destination, string Sha256);
    private sealed record Manifest(int SchemaVersion, string UnityVersion, string BuildTarget, string XrBackend, string? Provider, List<FileEntry> Files);

    /// <summary>Apply explicitly supplied, hash-pinned OpenXR provider files to a project candidate.</summary>
    public static void Apply(string manifestPath, string candidate, string buildTarget, string unityVersion = ConversionConfig.TargetVersion)
    {
        var fullManifest = Path.GetFullPath(manifestPath); if (!File.Exists(fullManifest)) throw new FileNotFoundException("Integration manifest not found.", fullManifest);
        var sourceRoot = Path.GetDirectoryName(fullManifest)!;
        if (new FileInfo(fullManifest).LinkTarget is not null) throw new InvalidDataException("Integration manifest symlinks are not supported.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(fullManifest), ConversionConfig.Json)
            ?? throw new InvalidDataException("Integration manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.UnityVersion != unityVersion || manifest.BuildTarget != buildTarget || manifest.XrBackend != "OpenXR")
            throw new InvalidDataException("Integration manifest must target schema 1, Unity " + unityVersion + ", build target " + buildTarget + ", and OpenXR.");
        var hasProvider = !string.IsNullOrWhiteSpace(manifest.Provider);
        if (buildTarget == "StandaloneOSX" && manifest.Provider != MetalBridge)
            throw new InvalidDataException("StandaloneOSX requires the integration emitted by tools/UnityMetalCli build.");
        if (buildTarget != "StandaloneWindows64" && !hasProvider)
            throw new InvalidDataException("Non-Windows OpenXR builds require Provider to name a managed XR loader class.");
        if (hasProvider && !Regex.IsMatch(manifest.Provider!, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
            throw new InvalidDataException("Provider must be a fully qualified managed XR loader type name.");
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("Integration manifest must list at least one hash-pinned provider file.");
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var copies = new List<(string Source, string Destination, string Sha256)>();
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Destination) || !System.Text.RegularExpressions.Regex.IsMatch(entry.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Each integration file requires source, destination, and a SHA-256 hash.");
            var source = SafeFiles.Inside(sourceRoot, entry.Source);
            if (!File.Exists(source)) throw new FileNotFoundException("Integration source file not found.", source);
            RejectLinks(sourceRoot, source);
            var actual = SafeFiles.Hash(source);
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Integration source hash mismatch: " + entry.Source);
            var destRelative = entry.Destination.Replace('\\', '/');
            if (!(destRelative.StartsWith("Assets/Plugins/", StringComparison.Ordinal) || destRelative.StartsWith("Assets/OpenXRProvider/", StringComparison.Ordinal)))
                throw new InvalidDataException("Integration destinations are limited to Assets/Plugins or Assets/OpenXRProvider: " + entry.Destination);
            var destination = SafeFiles.Inside(candidate, destRelative);
            if (!seen.Add(destination)) throw new InvalidDataException("Duplicate integration destination: " + destRelative);
            RejectLinks(candidate, destination);
            if (File.Exists(destination))
            {
                if (!SafeFiles.Hash(destination).Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new IOException("Integration refuses to overwrite a different existing file: " + destRelative);
                continue;
            }
            copies.Add((source, destination, actual));
        }
        if (buildTarget == "StandaloneOSX")
            foreach (var required in RequiredMetalFiles())
                if (!seen.Contains(SafeFiles.Inside(candidate, required)))
                    throw new InvalidDataException("The Metal OpenXR integration is missing " + required);
        string? settings = null;
        string? content = null;
        if (hasProvider)
        {
            settings = SafeFiles.Inside(candidate, "ProjectSettings/H3VRProvider.json");
            RejectLinks(candidate, settings);
            content = JsonSerializer.Serialize(new { loader = manifest.Provider }, ConversionConfig.Json) + "\n";
            if (File.Exists(settings) && File.ReadAllText(settings) != content) throw new IOException("Candidate already contains different H3VR provider configuration.");
        }
        foreach (var copy in copies)
        {
            SafeFiles.Copy(copy.Source, copy.Destination);
            if (SafeFiles.Hash(copy.Destination) != copy.Sha256)
                throw new IOException("Integration source changed while copying " + copy.Source);
        }
        if (settings != null && content != null && !File.Exists(settings))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, content);
        }
    }

    internal static void StageGraphicsUpgradeMetalRenderer(string repository, string candidate)
    {
        // Apply validates the integration hashes first. Only the private candidate
        // gets the render-pipeline compatibility code from this converter checkout.
        var destination = SafeFiles.Inside(candidate, MetalRendererSource);
        RejectLinks(candidate, destination);
        if (!File.Exists(destination))
            throw new FileNotFoundException("The macOS integration did not stage its stereo renderer.", destination);
        var source = Path.Combine(repository, "runtime/UnityMetal/NativeStereoRenderer.cs");
        File.Copy(source, destination, overwrite: true);
    }

    private static void RejectLinks(string root, string path)
    {
        root = Path.GetFullPath(root);
        for (var cursor = path; cursor != root && SafeFiles.Contains(root, cursor); cursor = Path.GetDirectoryName(cursor)!)
        {
            FileSystemInfo item = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);
            if (item.LinkTarget is not null) throw new InvalidDataException("Integration paths cannot contain symlinks: " + cursor);
        }
    }
}
