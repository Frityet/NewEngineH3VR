using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildMetalSmoke
{
    public static void Build()
    {
        IndexRawPose.Verify();
        string[] args = Environment.GetCommandLineArgs();
        string output = args[Array.IndexOf(args, "-smokeOutput") + 1];
        const BuildTarget target = BuildTarget.StandaloneOSX;
        PlayerSettings.companyName = "NativeH3VRTest";
        PlayerSettings.productName = "MetalSmoke-" + Path.GetFileName(Directory.GetCurrentDirectory());
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Metal });
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture", "ARM64");
        PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 1);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.enableFrameTimingStats = true;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.runInBackground = true;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var origin = new GameObject("Synthetic tracking origin");
        var camera = new GameObject("Metal smoke camera");
        camera.transform.SetParent(origin.transform);
        var view = camera.AddComponent<Camera>();
        view.clearFlags = CameraClearFlags.SolidColor;
        view.backgroundColor = new Color(.04f, .10f, .24f);
        view.nearClipPlane = .05f;
        camera.AddComponent<MetalSmoke>();

        // Independently authored colored geometry, with a near binocular marker
        // and asymmetric vertical detail for external composited-eye inspection.
        AddMarker("Near red", new Vector3(0, 1.6f, 1.1f), Vector3.one * .18f, Color.red);
        AddMarker("High green", new Vector3(.27f, 1.94f, 1.25f), Vector3.one * .13f, Color.green);
        const string scenePath = "Assets/MetalSmoke.unity";
        if (!EditorSceneManager.SaveScene(scene, scenePath))
            throw new InvalidOperationException("Failed to save synthetic Metal test scene");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = output,
            target = target,
            options = BuildOptions.StrictMode,
        });
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException(report.summary.result.ToString());
    }

    static void AddMarker(string name, Vector3 position, Vector3 scale, Color color)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = name;
        marker.transform.position = position;
        marker.transform.localScale = scale;
        var material = new Material(Shader.Find("Unlit/Color")) { color = color };
        AssetDatabase.CreateAsset(material, "Assets/" + name + ".mat");
        marker.GetComponent<Renderer>().sharedMaterial = material;
    }
}
