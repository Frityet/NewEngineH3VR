using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
public static class EnlightenProbe {
    public static void Build() {
        var scene = EditorSceneManager.OpenScene("Assets/Probe.unity");
        Lightmapping.lightingDataAsset =
            AssetDatabase.LoadAssetAtPath<LightingDataAsset>("Assets/GI.asset");
        EditorSceneManager.SaveScene(scene);
        PlayerSettings.productName = "EnlightenRuntimeProbe";
        PlayerSettings.companyName = "NativeH3VRLocal";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { UnityEngine.Rendering.GraphicsDeviceType.Metal });
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        var result = BuildPipeline.BuildPlayer(
            new[] { "Assets/Probe.unity" },
            Path.GetFullPath("../build/lighting/EnlightenRuntimeProbe.app"),
            BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("GI_BUILD_RESULT " + result.summary.result);
        EditorApplication.Exit(
            result.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
    public static void Run() {
        var a = AssetDatabase.LoadAssetAtPath<LightingDataAsset>("Assets/GI.asset");
        var s = new SerializedObject(a);
        File.WriteAllText(Path.GetFullPath("../build/lighting/gi-editor.json"),
                          EditorJsonUtility.ToJson(a, true));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        string result =
            "valid=" + typeof(LightingDataAsset).GetProperty("isValid", flags).GetValue(a, null) +
            " error=" +
            typeof(LightingDataAsset).GetProperty("validityErrorMessage", flags).GetValue(a, null);
        File.WriteAllText(Path.GetFullPath("../build/lighting/gi-validation.txt"), result);
        Debug.Log("ENLIGHTEN_GI_PROBE " + result);
        EditorApplication.Exit(0);
    }
}
