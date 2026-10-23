using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.IO;

public static class BuildNativeProbe {
    public static void Build() {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.backgroundColor = new Color(0.025f, 0.035f, 0.055f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.transform.position = new Vector3(0, 1, -4);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.position = new Vector3(0, 0, 1);
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/raw.asset");
        if (texture == null || texture.width == 0)
            throw new Exception("Native texture load failed");
        Debug.Log("NATIVE_TEXTURE loaded=" + texture.name + " size=" + texture.width + "x" +
                  texture.height + " format=" + texture.format);
        var material = new Material(Shader.Find("Unlit/Texture"));
        material.mainTexture = texture;
        cube.GetComponent<Renderer>().sharedMaterial = material;
        var light = new GameObject("Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(40, 25, 0);
        new GameObject("Native probe").AddComponent<NativeEngineProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/NativeEngineProbe.unity");
        PlayerSettings.productName = "NativeH3VR Engine Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.engineprobe");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        // Unity 2020.3 uses 0=x64, 1=arm64, 2=universal for macOS.
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 900;
        PlayerSettings.defaultScreenHeight = 600;
        PlayerSettings.runInBackground = true;
        var output = Environment.GetEnvironmentVariable("NATIVEH3VR_BUILD_OUTPUT");
        if (string.IsNullOrEmpty(output))
            throw new Exception("NATIVEH3VR_BUILD_OUTPUT is required");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/NativeEngineProbe.unity" }, output,
                                               BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("NATIVEH3VR_BUILD_RESULT " + report.summary.result);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
