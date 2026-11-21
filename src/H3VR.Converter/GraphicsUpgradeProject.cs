using System.Text.Json;

namespace H3VR.Converter;

internal static class GraphicsUpgradeProject
{
    internal const string PackageName = "com.unity.render-pipelines.universal";
    internal const string PackageVersion = "17.7.0";
    internal const string RuntimeSource = "runtime/GraphicsUpgrade";
    internal const string RuntimeAssets = "Assets/NativeH3VR/GraphicsUpgrade";
    internal const string GeneratedAssets = RuntimeAssets + "/Generated";
    internal const string RuntimeAssembly = "NativeH3VR.GraphicsUpgrade";
    internal const string Marker = "ProjectSettings/NativeH3VRGraphicsUpgrade.json";

    // URP 17.7 creates these two defaults at the Assets root. Its build
    // preprocessor serializes the populated default volume and global settings.
    internal static void RefreshGeneratedDefaults(string project, string recovered, Dictionary<string, string> files)
    {
        if (!Directory.Exists(recovered))
            return;
        foreach (string relative in new[] { "Assets/UniversalRenderPipelineGlobalSettings.asset", "Assets/DefaultVolumeProfile.asset" })
        {
            string source = SafeFiles.Inside(recovered, relative);
            if (Path.Exists(source) || Path.Exists(source + ".meta"))
                continue;
            string generated = SafeFiles.Inside(project, relative);
            var file = new FileInfo(generated);
            if (files.ContainsKey(generated) && file.Exists && file.LinkTarget == null)
                files[generated] = SafeFiles.Hash(generated);
        }
    }

    internal static bool IsEditorSource(string path) => Path.GetFileName(path) is
        "GraphicsUpgradeConfiguration.cs" or "GraphicsUpgradeMaterials.cs" or "GraphicsUpgradeThermalShaders.cs"
        or "LegacyVertexLightBridge.cs" or "ThermalPackageVariants.cs";

    internal static bool IsOptionalSource(string relativePath) =>
        relativePath.StartsWith(RuntimeSource + "/", StringComparison.Ordinal) ||
        relativePath == "src/H3VR.Converter/GraphicsOptionsHook.cs" ||
        relativePath == "src/H3VR.Converter/StockThermalScopeHook.cs" ||
        relativePath.StartsWith("editor/", StringComparison.Ordinal) && IsEditorSource(relativePath);

    internal static bool IsRuntimeAsset(string path) =>
        Path.GetExtension(path) is ".cs" or ".shader" or ".hlsl" or ".meta" &&
        !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    internal static string EditorPackageManifest(ConversionConfig config) =>
        Path.Combine(config.UnityData, "Resources", "PackageManager", "BuiltInPackages", PackageName, "package.json");

    internal static void VerifyEditorPackage(ConversionConfig config)
    {
        var manifest = EditorPackageManifest(config);
        if (File.Exists(manifest))
        {
            using var package = JsonDocument.Parse(File.ReadAllText(manifest));
            if (package.RootElement.TryGetProperty("name", out var name) && name.GetString() == PackageName &&
                package.RootElement.TryGetProperty("version", out var version) && version.GetString() == PackageVersion)
                return;
        }
        throw new InvalidDataException($"--graphics-upgrade requires a Unity 6.7 editor that includes {PackageName}@{PackageVersion}: {manifest}");
    }

    internal static void RequireWorkspaceMode(ConversionConfig config)
    {
        if (!Directory.Exists(config.Project)) return;
        bool previousUpgrade = File.Exists(Path.Combine(config.Project, Marker));
        if (previousUpgrade != config.GraphicsUpgrade)
            throw new InvalidDataException(
                "The workspace already contains a project with a different graphics pipeline. " +
                "Select a fresh --workspace and use --reuse-extraction to reuse a verified cold extraction.");
    }
}
