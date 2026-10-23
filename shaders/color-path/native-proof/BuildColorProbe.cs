using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
public static class BuildColorProbe {
    public static void Build() {
        var s = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        new GameObject("Actual Uber LUT coordinate proof").AddComponent<ColorProbe>();
        EditorSceneManager.SaveScene(s, "Assets/Probe.unity");
        PlayerSettings.productName = "Color Path Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.colorpath");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 320;
        PlayerSettings.defaultScreenHeight = 240;
        PlayerSettings.runInBackground = true;
        var r = BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" },
                                          Path.GetFullPath("output/ColorPath.app"),
                                          BuildTarget.StandaloneOSX, BuildOptions.Development);
        EditorApplication.Exit(r.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
