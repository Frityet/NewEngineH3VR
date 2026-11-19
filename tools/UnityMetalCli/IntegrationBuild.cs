using System.Text.Json;
using H3VR.Converter;

namespace UnityMetalCli;

internal static class IntegrationBuild
{
    internal const string Library = "libNativeH3VRMetal.dylib";

    internal static readonly string[] BridgeSources = ["MetalOpenXR.cs", "NativeStereoRenderer.cs"];

    internal static string SourceRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "runtime", "UnityMetal", "MetalOpenXR.mm")))
                    return directory.FullName;
        }
        throw new DirectoryNotFoundException("Use the tool from its NativeH3VR source checkout to build the Metal plug-in.");
    }

    internal static async Task BuildPluginAsync(string editor, string headers, string output, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Building the Apple Silicon Metal plug-in requires macOS and Xcode.");
        string unity = Directory.GetParent(editor)?.Parent?.FullName
            ?? throw new InvalidDataException("Supply the Unity editor executable.");
        string pluginApi = Path.Combine(unity, "PluginAPI");
        if (!File.Exists(Path.Combine(pluginApi, "IUnityGraphicsMetal.h")))
            throw new InvalidDataException("Unity editor is missing its Metal plug-in API headers.");
        if (!File.Exists(Path.Combine(headers, "openxr", "openxr_platform.h")))
            throw new InvalidDataException("Supply the Khronos OpenXR SDK include directory with Metal support.");

        string source = Path.Combine(SourceRoot(), "runtime", "UnityMetal", "MetalOpenXR.mm");
        await ChildProcess.CheckedAsync("/usr/bin/xcrun",
            ["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror", "-fobjc-arc", "-arch", "arm64",
             "-mmacosx-version-min=13.0", "-dynamiclib", $"-Wl,-install_name,@rpath/{Library}",
             "-I", pluginApi, "-I", headers, "-framework", "Foundation", "-framework", "Metal",
             source, "-o", output], cancellation);
        await ChildProcess.CheckedAsync("/usr/bin/codesign", ["--force", "--sign", "-", output], cancellation);
        await RequireArm64Async(output, cancellation);
    }

    internal static async Task RequireArm64Async(string file, CancellationToken cancellation)
    {
        string architectures = (await ChildProcess.CheckedAsync("/usr/bin/lipo", ["-archs", file], cancellation,
            captureOutput: true)).Trim();
        if (architectures != "arm64")
            throw new InvalidDataException($"Expected a thin arm64 executable or library, found {architectures}: {file}");
    }

    internal static async Task<string> BuildAsync(
        string editor, string headers, string platform, string output, CancellationToken cancellation)
    {
        editor = SafeFiles.Canonical(editor);
        headers = SafeFiles.Canonical(headers);
        platform = SafeFiles.Canonical(platform);
        output = SafeFiles.Canonical(output);
        if (File.Exists(output) || Directory.Exists(output) || new FileInfo(output).LinkTarget is not null)
            throw new IOException("Choose a new integration output directory: " + output);
        string parent = Path.GetDirectoryName(output)!;
        string staging = Path.Combine(parent, ".unity-metal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await BuildPluginAsync(editor, headers, Path.Combine(staging, Library), cancellation);
            File.WriteAllText(Path.Combine(staging, Library + ".meta"), PluginMetadata);
            var entries = new List<Entry>();
            foreach (string file in new[] { Library, Library + ".meta" })
                entries.Add(new(file, "Assets/Plugins/macOS/" + file, SafeFiles.Hash(Path.Combine(staging, file))));

            string sourceRoot = Path.Combine(SourceRoot(), "runtime", "UnityMetal");
            foreach (string source in BridgeSources.Concat(["H3VRMetalBinding.cs", "SyntheticHands.cs", "StockGearNormals.cs"]))
            {
                string destination = Path.Combine(staging, source);
                File.Copy(Path.Combine(sourceRoot, source), destination);
                entries.Add(new(source, "Assets/OpenXRProvider/" + source, SafeFiles.Hash(destination)));
            }
            entries.AddRange(StagePlatform(platform, staging));
            var manifest = new Manifest(1, ConversionConfig.TargetVersion, "StandaloneOSX", "OpenXR", "NativeH3VR.UnityMetal", entries);
            SafeFiles.AtomicJson(Path.Combine(staging, "integration.json"), manifest);
            Directory.Move(staging, output);
            return Path.Combine(output, "integration.json");
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static List<Entry> StagePlatform(string platform, string staging)
    {
        string manifest = Path.Combine(platform, "platform.json");
        var fragment = JsonSerializer.Deserialize<PlatformFragment>(File.ReadAllText(manifest), ConversionConfig.Json);
        if (fragment?.Files is null)
            throw new InvalidDataException("Missing platform manifest file list.");
        string[] libraries = ["libsteam_api.dylib", "libCSteamworks.dylib", "libfmod.dylib", "libfmodstudio.dylib", "libNativeH3VRFMOD.dylib"];
        var missing = new HashSet<string>(libraries.Concat(libraries.Select(name => name + ".meta")), StringComparer.Ordinal);
        var entries = new List<Entry>();
        string destinationDirectory = Path.Combine(staging, "platform");
        Directory.CreateDirectory(destinationDirectory);
        foreach (Entry entry in fragment.Files)
        {
            if (entry?.Source is not { } name || !missing.Remove(name))
                throw new InvalidDataException("Unknown or duplicate platform plug-in filename: " + entry?.Source);
            string destination = "Assets/Plugins/Native/macOS/" + name;
            if (entry.Destination != destination)
                throw new InvalidDataException("Invalid platform plug-in destination for " + name);
            string source = Path.Combine(platform, name);
            if (new FileInfo(source).LinkTarget is not null || SafeFiles.Hash(source) != entry.Sha256)
                throw new InvalidDataException("Platform fragment hash mismatch for " + name);
            string copied = Path.Combine(destinationDirectory, name);
            File.Copy(source, copied);
            string sha256 = SafeFiles.Hash(copied);
            if (sha256 != entry.Sha256)
                throw new IOException("Platform source changed while copying " + name);
            entries.Add(new("platform/" + name, destination, sha256));
        }
        if (missing.Count != 0)
            throw new InvalidDataException("Platform manifest is missing: " + string.Join(", ", missing.Order(StringComparer.Ordinal)));
        return entries;
    }

    private sealed record Entry(string Source, string Destination, string Sha256);
    private sealed record PlatformFragment(List<Entry> Files);
    private sealed record Manifest(int SchemaVersion, string UnityVersion, string BuildTarget, string XrBackend, string Provider, List<Entry> Files);

    internal const string PluginMetadata = """
        fileFormatVersion: 2
        guid: c7d1ecdb3ff056ae8da0bb1f10b279fa
        PluginImporter:
          externalObjects: {}
          serializedVersion: 2
          iconMap: {}
          executionOrder: {}
          defineConstraints: []
          isPreloaded: 1
          isOverridable: 0
          isExplicitlyReferenced: 0
          validateReferences: 1
          platformData:
          - first:
              Any:
            second:
              enabled: 0
              settings: {}
          - first:
              Editor: Editor
            second:
              enabled: 0
              settings: {}
          - first:
              Standalone: OSXUniversal
            second:
              enabled: 1
              settings:
                CPU: ARM64
          userData:
          assetBundleName:
          assetBundleVariant:
        """ + "\n";
}
