using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
public static class ProbeBuild {
    public static void Build() { BuildImpl(false); }
    public static void BuildGame() { BuildImpl(true); }
    static void BuildImpl(bool game) {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (game)
            new GameObject("Native Game Bridge Probe").AddComponent<NativeGameBridgeProbe>();
        else
            new GameObject("Native Metal Probe").AddComponent<NativeMetalProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        PlayerSettings.productName = "Native H3VR Metal Probe";
        PlayerSettings.companyName = "NativeH3VRLocal";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 512;
        PlayerSettings.defaultScreenHeight = 256;
        PlayerSettings.runInBackground = true;
        foreach (string plugin in new[] { "libNativeH3VRXR.dylib",
                                          "libNativeH3VRTracking.dylib" }) {
            var importer = (PluginImporter)AssetImporter.GetAtPath("Assets/Plugins/" + plugin);
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
            importer.SetPlatformData(BuildTarget.StandaloneOSX, "CPU", "ARM64");
            importer.SaveAndReimport();
        }
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" },
                                               game ? "output/NativeGameXRProbe.app"
                                                    : "output/NativeXRProbe.app",
                                               BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("PROBE_BUILD_RESULT " + report.summary.result);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
