using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.IO;
using System.Linq;
public static class BuildAlloyProbe {
    public static void Build() {
        var shader =
            AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/Shaders/Alloy_Core.shader");
        if (shader == null)
            throw new Exception("Alloy shader missing");
        Directory.CreateDirectory("Assets/Resources/Variants");
        var sets = File.ReadAllLines("Assets/Resources/AlloyMaterialKeywords.txt");
        int index = 0;
        foreach (var key in sets) {
            var material = new Material(shader);
            material.shaderKeywords =
                key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            AssetDatabase.CreateAsset(material, "Assets/Resources/Variants/Original_" +
                                                    (index++).ToString("D3") + ".mat");
        }
        // Explicitly retain the normal-map off/on pair for the quantitative feature check.
        foreach (var keyword in new[] { "", "EFFECT_BUMP" }) {
            var material = new Material(shader);
            material.shaderKeywords = keyword.Length == 0 ? new string[0] : new[] { keyword };
            AssetDatabase.CreateAsset(material,
                                      "Assets/Resources/Variants/Feature_" + (index++) + ".mat");
        }
        var unlit =
            AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/Shaders/Alloy_Unlit.shader");
        foreach (var key in File.ReadAllLines("Assets/Resources/AlloyUnlitKeywords.txt")) {
            var material = new Material(unlit);
            material.shaderKeywords =
                key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            AssetDatabase.CreateAsset(material,
                                      "Assets/Resources/Variants/Unlit_" + (index++) + ".mat");
        }
        foreach (var spec in File.ReadAllLines("Assets/Resources/AlloyOtherKeywords.txt")) {
            var parts = spec.Split('|');
            var other = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/Shaders/" +
                                                              parts[0] + ".shader");
            var material = new Material(other);
            material.shaderKeywords =
                parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            AssetDatabase.CreateAsset(material,
                                      "Assets/Resources/Variants/Other_" + (index++) + ".mat");
        }
        foreach (var spec in File.ReadAllLines("Assets/Resources/AlloyCompatKeywords.txt")) {
            var parts = spec.Split('|');
            var other = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/Shaders/" +
                                                              parts[0] + ".shader");
            var material = new Material(other);
            material.shaderKeywords =
                parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            AssetDatabase.CreateAsset(material,
                                      "Assets/Resources/Variants/Compat_" + (index++) + ".mat");
        }
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        new GameObject("Alloy native audit").AddComponent<AlloyGpuProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/AlloyProbe.unity");
        PlayerSettings.productName = "NativeH3VR Alloy Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.alloyprobe");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 640;
        PlayerSettings.defaultScreenHeight = 480;
        PlayerSettings.runInBackground = true;
        var output = Path.GetFullPath("../shaders/AlloyNativeProbe.app");
        var result = BuildPipeline.BuildPlayer(new[] { "Assets/AlloyProbe.unity" }, output,
                                               BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("ALLOY_BUILD " + result.summary.result);
        EditorApplication.Exit(result.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
