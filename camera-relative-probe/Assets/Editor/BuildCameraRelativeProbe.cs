using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
public static class BuildCameraRelativeProbe {
    public static void Build() {
        NativeOriginalVariantFilter.Configure();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        new GameObject("Original camera relative variants").AddComponent<CameraRelativeGpuProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        PlayerSettings.productName = "NativeH3VR Camera Relative Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.camerarelativeprobe");
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
        var result =
            BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" },
                                      Path.GetFullPath("../shaders/CameraRelativeNativeProbe.app"),
                                      BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("CAMERA_RELATIVE_BUILD " + result.summary.result);
        EditorApplication.Exit(result.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
