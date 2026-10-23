using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using System.Linq;
using System.Collections.Generic;
public static class BuildHistoricalShadowProbe {
    public static void Build() {
        Directory.CreateDirectory("Assets/Resources/HistoricalShadowMaterials");
        var report = JsonUtility.FromJson<HistoricalShadowAudit.Report>(
            File.ReadAllText("../shaders/alloy-historical-shadowmask-draw-report.json"));
        var collection = new ShaderVariantCollection();
        int count = 0;
        foreach (var row in report.draws) {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/Shaders/" +
                                                               row.shader + ".shader");
            var material = new Material(shader);
            material.shaderKeywords =
                row.keywords.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            AssetDatabase.CreateAsset(material, "Assets/Resources/HistoricalShadowMaterials/" +
                                                    (count++) + ".mat");
            var known = new HashSet<string>(shader.keywordSpace.keywordNames);
            var selected = material.shaderKeywords.Where(k => known.Contains(k)).ToArray();
            try {
                collection.Add(new ShaderVariantCollection.ShaderVariant(
                    shader, PassType.ForwardBase, selected));
            } catch (System.Exception) {
                Debug.LogError("SVC_REJECTED " + row.shader +
                               " keys=" + string.Join(" ", selected));
                throw;
            }
        }
        AssetDatabase.CreateAsset(collection, "Assets/Resources/HistoricalShadow.shadervariants");
        var graphics = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
        graphics.FindProperty("m_LightmapStripping").intValue = 1;
        graphics.FindProperty("m_FogStripping").intValue = 1;
        foreach (string name in new[] { "m_LightmapKeepPlain", "m_LightmapKeepDirCombined",
                                        "m_LightmapKeepDynamicPlain",
                                        "m_LightmapKeepDynamicDirCombined",
                                        "m_LightmapKeepShadowMask", "m_LightmapKeepSubtractive",
                                        "m_FogKeepLinear", "m_FogKeepExp", "m_FogKeepExp2" })
            graphics.FindProperty(name).boolValue = true;
        graphics.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        new GameObject("Original cliff variants").AddComponent<HistoricalShadowGpuProbe>();
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        PlayerSettings.productName = "NativeH3VR Historical Shadow Probe";
        PlayerSettings.companyName = "LocalPort";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.historicalshadowprobe");
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
            Path.GetFullPath("../shaders/HistoricalShadowNativeProbe.app"),
            BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("HISTORICAL_SHADOWMASK_BUILD " + result.summary.result);
        EditorApplication.Exit(result.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
