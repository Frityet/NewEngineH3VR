using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using System;
using System.IO;
using System.Linq;
public static class BuildUIProbe {
    static void Ids(string phase) {
        var ids =
            AssetDatabase.LoadAllAssetsAtPath("Assets/Plugins/UnityEngine.UI.dll")
                .OfType<MonoScript>()
                .Select(s => {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string guid, out long id);
                    return s.GetClass().FullName + " " + guid + " " + id;
                })
                .OrderBy(s => s);
        File.WriteAllLines("../../../build/ugui/" + phase + "-script-ids.txt", ids);
    }
    public static void Prepare() {
        Ids("old");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var canvas = new GameObject("Canvas", typeof(Canvas)).GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var t = new GameObject("SerializedText", typeof(RectTransform), typeof(Text))
                    .GetComponent<Text>();
        t.transform.SetParent(canvas.transform, false);
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.text = "Native H3VR UI";
        t.fontSize = 24;
        t.rectTransform.sizeDelta = new Vector2(300, 120);
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        new GameObject("Probe").AddComponent<UIProbe>();
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
                                     "Assets/Probe.unity");
        EditorApplication.Exit(0);
    }
    public static void Build() {
        Ids("new");
        var s = EditorSceneManager.OpenScene("Assets/Probe.unity");
        if (UnityEngine.Object.FindObjectOfType<Text>() == null)
            throw new Exception("Old serialized Text did not resolve");
        PlayerSettings.productName = "Native H3VR UI Probe";
        PlayerSettings.companyName = "LocalPort";
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
        var r = BuildPipeline.BuildPlayer(new[] { "Assets/Probe.unity" },
                                          Path.GetFullPath("../../../build/ugui/UIProbe.app"),
                                          BuildTarget.StandaloneOSX, BuildOptions.Development);
        EditorApplication.Exit(r.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
