namespace H3VR.Converter;

public static class SourceStaging
{
    public const string ShaderVariantCatalogName = "shader-variant-domains.json";
    internal const string EditorAssets = "Assets/Editor/H3VRConverter";

    public static void Install(string repository, string project, string buildTarget)
    {
        var editor = Path.Combine(project, EditorAssets);
        Directory.CreateDirectory(editor);
        foreach (var file in Directory.GetFiles(Path.Combine(repository, "editor"), "*.cs")) SafeFiles.Copy(file, Path.Combine(editor, Path.GetFileName(file)));
        bool nativeMetal = buildTarget == "StandaloneOSX";
        SafeFiles.AtomicJson(Path.Combine(editor, "H3VRConverter.Editor.asmdef"), new
        {
            name = "H3VRConverter.Editor",
            references = nativeMetal ? Array.Empty<string>() :
                new[] { "Unity.XR.Management", "Unity.XR.Management.Editor", "Unity.XR.OpenXR", "Unity.XR.OpenXR.Editor" },
            versionDefines = nativeMetal ? Array.Empty<object>() :
                new[] { new { name = "com.unity.xr.openxr", expression = "1.13.0", define = "NATIVEH3VR_UNITY_OPENXR" } },
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
}
