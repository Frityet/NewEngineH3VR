namespace H3VR.Converter;

public static class SourceStaging
{
    public const string ShaderVariantCatalogName = "shader-variant-domains.json";
    internal const string EditorAssets = "Assets/Editor/H3VRConverter";

    public static void Install(string repository, string project, string buildTarget, bool buildProbe = false, bool graphicsUpgrade = false)
    {
        var editor = Path.Combine(project, EditorAssets);
        Directory.CreateDirectory(editor);
        foreach (var file in Directory.GetFiles(Path.Combine(repository, "editor"), "*.cs"))
        {
            if (!graphicsUpgrade && GraphicsUpgradeProject.IsEditorSource(file)) continue;
            SafeFiles.Copy(file, Path.Combine(editor, Path.GetFileName(file)));
        }
        bool nativeMetal = buildTarget == "StandaloneOSX";
        bool unityXr = !nativeMetal && !buildProbe;
        var references = new List<string>();
        var versionDefines = new List<object>();
        if (unityXr)
        {
            references.AddRange(["Unity.XR.Management", "Unity.XR.Management.Editor", "Unity.XR.OpenXR", "Unity.XR.OpenXR.Editor"]);
            versionDefines.Add(new { name = "com.unity.xr.openxr", expression = ProjectMigration.OpenXrPackageVersion, define = "NATIVEH3VR_UNITY_OPENXR" });
        }
        if (graphicsUpgrade)
        {
            references.AddRange(["Unity.RenderPipelines.Core.Runtime", "Unity.RenderPipelines.Core.Editor",
                "Unity.RenderPipelines.Universal.Runtime", "Unity.RenderPipelines.Universal.Editor", GraphicsUpgradeProject.RuntimeAssembly]);
            versionDefines.Add(new
            {
                name = GraphicsUpgradeProject.PackageName,
                expression = GraphicsUpgradeProject.PackageVersion,
                define = "NATIVEH3VR_GRAPHICS_UPGRADE"
            });
            InstallGraphicsUpgrade(repository, project);
        }
        SafeFiles.AtomicJson(Path.Combine(editor, "H3VRConverter.Editor.asmdef"), new
        {
            name = "H3VRConverter.Editor",
            references,
            versionDefines,
            includePlatforms = new[] { "Editor" },
            overrideReferences = true,
            autoReferenced = false
        });
        if (nativeMetal)
        {
            var modHost = Path.Combine(project, "Assets/H3VRConverter/NativeModHost.cs");
            SafeFiles.Copy(Path.Combine(repository, "runtime/ModHost/NativeModHost.cs"), modHost);
            return;
        }
        if (!unityXr) return;
        var features = Path.Combine(project, "Assets/H3VRConverter/OpenXR");
        Directory.CreateDirectory(features);
        foreach (var file in Directory.GetFiles(Path.Combine(repository, "runtime/OpenXR/Unity"), "*.cs")) SafeFiles.Copy(file, Path.Combine(features, Path.GetFileName(file)));
        SafeFiles.AtomicJson(Path.Combine(features, "H3VRConverter.OpenXRFeatures.asmdef"), new
        {
            name = "H3VRConverter.OpenXRFeatures",
            references = new[] { "Unity.XR.OpenXR", "Unity.InputSystem" },
            precompiledReferences = new[] { "NativeH3VR.OpenXR.dll" },
            overrideReferences = true
        });
    }

    private static void InstallGraphicsUpgrade(string repository, string project)
    {
        var source = Path.Combine(repository, GraphicsUpgradeProject.RuntimeSource);
        var destination = Path.Combine(project, GraphicsUpgradeProject.RuntimeAssets);
        foreach (var file in SafeFiles.Files(source).Where(GraphicsUpgradeProject.IsRuntimeAsset))
            SafeFiles.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        SafeFiles.AtomicJson(Path.Combine(destination, GraphicsUpgradeProject.RuntimeAssembly + ".asmdef"), new
        {
            name = GraphicsUpgradeProject.RuntimeAssembly,
            references = new[] { "Unity.RenderPipelines.Core.Runtime", "Unity.RenderPipelines.Universal.Runtime" }
        });
    }
}
