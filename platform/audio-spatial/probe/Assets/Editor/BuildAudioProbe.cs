using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.IO;
public static class BuildAudioProbe {
    public static void Build() {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        new GameObject("Listener").AddComponent<AudioListener>();
        new GameObject("Platform Probe").AddComponent<AudioStartupProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        PlayerSettings.productName = "NativeH3VR Audio Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.audioprobe");
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
        foreach (var guid in AssetDatabase.FindAssets("", new[] { "Assets/Plugins" })) {
            var importer =
                AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as PluginImporter;
            if (importer == null || !importer.assetPath.EndsWith(".dylib"))
                continue;
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
            importer.SetEditorData("OS", "OSX");
            importer.SetEditorData("CPU", "AnyCPU");
            importer.SetPlatformData(BuildTarget.StandaloneOSX, "CPU", "AnyCPU");
            importer.SaveAndReimport();
        }
        var output = Environment.GetEnvironmentVariable("NATIVEH3VR_BUILD_OUTPUT");
        if (string.IsNullOrEmpty(output))
            throw new Exception("output required");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" }, output,
                                               BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("PLATFORM_BUILD " + report.summary.result);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
