namespace H3VR.Converter;

public static class SourceStaging
{
    public static void Install(string repository, string project, string buildTarget)
    {
        var editor = Path.Combine(project, "Assets/Editor/H3VRConverter");
        Directory.CreateDirectory(editor);
        foreach (var file in Directory.GetFiles(Path.Combine(repository, "editor"), "*.cs")) SafeFiles.Copy(file, Path.Combine(editor, Path.GetFileName(file)));
        SafeFiles.AtomicJson(Path.Combine(editor, "H3VRConverter.Editor.asmdef"), new
        {
            name = "H3VRConverter.Editor",
            references = new[] { "Unity.XR.Management", "Unity.XR.Management.Editor", "Unity.XR.OpenXR", "Unity.XR.OpenXR.Editor" },
            includePlatforms = new[] { "Editor" },
            overrideReferences = true,
            autoReferenced = false
        });
        if (buildTarget == "StandaloneOSX")
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
