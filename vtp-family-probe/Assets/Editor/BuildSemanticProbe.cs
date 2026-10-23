using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
public static class BuildSemanticProbe {
    public static void Build() {
        NativeOriginalVariantFilter.Configure();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        new GameObject("Original cliff variants").AddComponent<SemanticGpuProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        PlayerSettings.productName = "NativeH3VR Semantic Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.vtpfamilyprobe");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 320;
        PlayerSettings.defaultScreenHeight = 240;
        PlayerSettings.runInBackground = true;
        var result = BuildPipeline.BuildPlayer(
            new[] { "Assets/Probe.unity" },
            Path.GetFullPath("../vtp-family-probe/output/NativeVTPFamily.app"),
            BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("CRITICAL_BUILD " + result.summary.result);
        EditorApplication.Exit(result.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
