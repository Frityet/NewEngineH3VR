using UnityEngine;
using UnityEditor;
using System;
using System.IO;
public static class NativeTextureBuild {
    public static void Build() {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        new GameObject("Native binary texture probe").AddComponent<NativeTextureProbe>();
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        var texture =
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/OriginalDXT1.asset");
        var cube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Resources/OriginalBC6H.asset");
        if (texture == null || cube == null || texture.format != TextureFormat.DXT1 ||
            cube.format != TextureFormat.BC6H)
            throw new Exception("Editor native binary import failed");
        Debug.Log("NATIVE_BINARY_EDITOR_IMPORT texture=" + texture.format + " cube=" + cube.format +
                  " cubeMips=" + cube.mipmapCount);
        PlayerSettings.productName = "Native Binary Texture Probe";
        PlayerSettings.companyName = "NativeH3VR";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { UnityEngine.Rendering.GraphicsDeviceType.Metal });
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 400;
        PlayerSettings.defaultScreenHeight = 200;
        PlayerSettings.runInBackground = true;
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" },
                                               "output/NativeBinaryTextureProbe.app",
                                               BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("NATIVE_BINARY_BUILD " + report.summary.result);
        EditorApplication.Exit(
            report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
    public static void Templates() {
        EditorSettings.serializationMode = SerializationMode.ForceBinary;
        var texture = new Texture2D(16, 16, TextureFormat.DXT1, true, true);
        texture.name = "Texture2D binary template";
        var cube = new Cubemap(16, TextureFormat.BC6H, true);
        cube.name = "Cubemap binary template";
        AssetDatabase.CreateAsset(texture, "Assets/TemplateTexture.asset");
        AssetDatabase.CreateAsset(cube, "Assets/TemplateCube.asset");
        AssetDatabase.SaveAssets();
        string output = Path.GetFullPath("../build/native-texture-template");
        File.Copy("Assets/TemplateTexture.asset", Path.Combine(output, "Texture2D.asset"), true);
        File.Copy("Assets/TemplateCube.asset", Path.Combine(output, "Cubemap.asset"), true);
        Debug.Log("NATIVE_BINARY_TEMPLATES_READY");
        EditorApplication.Exit(0);
    }
}
