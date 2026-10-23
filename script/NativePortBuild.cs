using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class NativePortBuild {
    [Serializable]
    public sealed class SceneInspection {
        public string path;
        public int objects, components, missing;
        public int expectedMissing, matchedOriginalMissing;
        public List<string> unexpectedMissing = new List<string>();
        public List<string> absentOriginalMissing = new List<string>();
        public bool lightingValid;
        public string lightingError, expectedLightingGuid, actualLightingGuid;
    }
    [Serializable]
    public sealed class MissingSlot {
        public long gameObjectFileID, componentFileID;
        public int componentIndex;
        public string gameObjectName;
        public string Key {
            get { return gameObjectFileID + ":" + componentIndex; }
        }
    }
    [Serializable]
    public sealed class SceneBaseline {
        public string path, sceneGuid;
        public int expectedMissingComponentCount;
        public List<MissingSlot> baseline;
    }
    [Serializable]
    public sealed class MissingScriptBaseline {
        public bool passed;
        public int sceneCount, expectedMissingComponentCount;
        public List<string> errors;
        public List<SceneBaseline> scenes;
    }
    [Serializable]
    public sealed class SceneInspections {
        public List<SceneInspection> scenes = new List<SceneInspection>();
    }
    [Serializable]
    public sealed class LightingLink {
        public string scene, lighting, lightingGuid;
    }
    [Serializable]
    public sealed class LightingLinks {
        public LightingLink[] links;
    }
    public static void InspectAll() {
        var baselinePath = Environment.GetEnvironmentVariable("NATIVEH3VR_SCENE_BASELINE");
        if (string.IsNullOrEmpty(baselinePath))
            baselinePath = Path.Combine(Application.dataPath,
                                        "../../build/scene-integrity/missing-script-baseline.json");
        var baseline = JsonUtility.FromJson<MissingScriptBaseline>(File.ReadAllText(baselinePath));
        if (baseline == null || !baseline.passed || baseline.errors == null ||
            baseline.errors.Count != 0 || baseline.scenes == null ||
            baseline.sceneCount != baseline.scenes.Count)
            throw new Exception(
                "A successful original/pristine/working missing-script audit is required");
        var enabledScenes = EditorBuildSettings.scenes.Where(s => s.enabled).ToArray();
        var baselineScenes = baseline.scenes.ToDictionary(s => s.path, StringComparer.Ordinal);
        var enabledPaths =
            new HashSet<string>(enabledScenes.Select(s => s.path), StringComparer.Ordinal);
        if (enabledPaths.Count != enabledScenes.Length ||
            !enabledPaths.SetEquals(baselineScenes.Keys) ||
            baseline.scenes.Sum(s => s.expectedMissingComponentCount) !=
                baseline.expectedMissingComponentCount)
            throw new Exception("Scene baseline coverage does not match the full build");
        var lightingPlan =
            Path.Combine(Application.dataPath, "../../build/lighting/scene-link-plan.json");
        var lightingLinks =
            JsonUtility
                .FromJson<LightingLinks>("{\"links\":" + File.ReadAllText(lightingPlan) + "}")
                .links.ToDictionary(link => link.scene, StringComparer.Ordinal);
        if (lightingLinks.Keys.Any(path => !baselineScenes.ContainsKey(path)))
            throw new Exception("Lighting plan contains an unknown scene");
        var results = new SceneInspections();
        foreach (var entry in enabledScenes) {
            if (string.IsNullOrEmpty(entry.path) || !File.Exists(entry.path))
                throw new Exception("Missing recovered scene path: " + entry.path);
            SceneBaseline sceneBaseline;
            if (!baselineScenes.TryGetValue(entry.path, out sceneBaseline) ||
                AssetDatabase.AssetPathToGUID(entry.path) != sceneBaseline.sceneGuid ||
                sceneBaseline.baseline == null ||
                sceneBaseline.baseline.Count != sceneBaseline.expectedMissingComponentCount)
                throw new Exception("Missing or invalid original scene baseline: " + entry.path);
            var expectedSlots = new HashSet<string>(sceneBaseline.baseline.Select(s => s.Key));
            if (expectedSlots.Count != sceneBaseline.baseline.Count)
                throw new Exception("Duplicate original missing-component identity: " + entry.path);
            var matchedSlots = new HashSet<string>();
            var scene = EditorSceneManager.OpenScene(entry.path);
            var result =
                new SceneInspection { path = entry.path, expectedMissing = expectedSlots.Count };
            foreach (var root in scene.GetRootGameObjects())
                foreach (var tr in root.GetComponentsInChildren<Transform>(true)) {
                    result.objects++;
                    var components = tr.GetComponents<Component>();
                    for (int slot = 0; slot < components.Length; slot++) {
                        result.components++;
                        if (components[slot] != null) {
                            NativeAssetIntegrity.ValidateRuntimeScript(components[slot],
                                                                       entry.path);
                            continue;
                        }
                        result.missing++;
                        var fileID =
                            unchecked((long)GlobalObjectId.GetGlobalObjectIdSlow(tr.gameObject)
                                          .targetObjectId);
                        var key = fileID + ":" + slot;
                        if (expectedSlots.Contains(key) && matchedSlots.Add(key))
                            result.matchedOriginalMissing++;
                        else
                            result.unexpectedMissing.Add(key + " " + tr.name);
                    }
                }
            result.absentOriginalMissing.AddRange(
                expectedSlots.Except(matchedSlots).OrderBy(s => s));
            var lighting = Lightmapping.lightingDataAsset;
            result.lightingValid = true;
            LightingLink expectedLighting;
            if (lightingLinks.TryGetValue(entry.path, out expectedLighting)) {
                result.expectedLightingGuid = expectedLighting.lightingGuid;
                result.actualLightingGuid =
                    lighting == null
                        ? null
                        : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(lighting));
                if (lighting == null || result.actualLightingGuid != result.expectedLightingGuid ||
                    AssetDatabase.AssetPathToGUID(expectedLighting.lighting) !=
                        result.expectedLightingGuid) {
                    result.lightingValid = false;
                    result.lightingError =
                        "Expected original lighting asset is missing or has changed identity";
                }
            }
            if (lighting != null) {
                const BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var validity = typeof(LightingDataAsset).GetProperty("isValid", flags);
                var error = typeof(LightingDataAsset).GetProperty("validityErrorMessage", flags);
                if (validity == null || error == null)
                    throw new MissingMemberException(
                        "Unity LightingDataAsset validity API is unavailable");
                bool valid = (bool)validity.GetValue(lighting, null);
                result.lightingValid &= valid;
                if (!valid)
                    result.lightingError = (string)error.GetValue(lighting, null);
            }
            results.scenes.Add(result);
            File.WriteAllText(
                Path.Combine(Application.dataPath, "../../build/all-scene-inspection.json"),
                JsonUtility.ToJson(results, true));
            Debug.Log("NATIVEH3VR_SCENE_AUDIT " + JsonUtility.ToJson(result));
            EditorUtility.UnloadUnusedAssetsImmediate();
        }
        var failures = results.scenes
                           .Where(s => s.unexpectedMissing.Count > 0 ||
                                       s.absentOriginalMissing.Count > 0 || !s.lightingValid)
                           .ToArray();
        if (failures.Length > 0)
            throw new Exception("Scene component/lighting inspection failed for " +
                                failures.Length +
                                " scenes: " + string.Join(", ", failures.Select(s => s.path)));
    }
    public static void Inspect() {
        var scenePaths = EditorBuildSettings.scenes.Select(s => s.path).ToArray();
        Debug.Log("NATIVEH3VR_SCENES " + string.Join("|", scenePaths));
        var main = scenePaths.FirstOrDefault();
        if (string.IsNullOrEmpty(main))
            throw new Exception("No recovered build scenes");
        var scene = EditorSceneManager.OpenScene(main);
        int objects = 0, components = 0, missing = 0;
        foreach (var root in scene.GetRootGameObjects())
            foreach (var tr in root.GetComponentsInChildren<Transform>(true)) {
                objects++;
                foreach (var component in tr.GetComponents<Component>()) {
                    components++;
                    if (component == null)
                        missing++;
                }
            }
        Debug.Log("NATIVEH3VR_SCENE_INSPECTION objects=" + objects + " components=" + components +
                  " missing=" + missing);
        File.WriteAllText(Path.Combine(Application.dataPath, "../../build/scene-inspection.json"),
                          "{\"objects\":" + objects + ",\"components\":" + components +
                              ",\"missing\":" + missing + "}\n");
        if (missing > 0)
            throw new Exception("Recovered main scene has " + missing + " missing components");
    }

    public static void BuildBoot() { Build(false); }
    public static void BuildFull() { Build(true); }
    [MenuItem("NativeH3VR/Run Full Content Preflight")]
    public static void BeginPreflightFull() {
        // Let the Editor tool request return before the longer read-only audit.
        EditorApplication.delayCall += PreflightFull;
    }
    public static void PreflightFull() {
        InspectAll();
        NativeAssetIntegrity.InspectAllPrefabs();
        Debug.Log("NATIVEH3VR_FULL_PREFLIGHT_SUCCESS");
        EditorApplication.Exit(0);
    }
    static void Build(bool full) {
        ForceComputeImports.Run();
        if (full)
            InspectAll();
        Inspect();
        PlayerSettings.productName = "NativeH3VR Experimental";
        // Separate save path from both the original game and Wine copy.
        PlayerSettings.companyName = "NativeH3VRLocal";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                                                "local.nativeh3vr.experimental");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone,
                                           ScriptingImplementation.Mono2x);
        // Original gameplay/save serializers discover types and members by reflection.
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone,
                                                ManagedStrippingLevel.Disabled);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        NativeOriginalVariantFilter.Configure();
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture",
                                                    "ARM64");
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.runInBackground = true;
        PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone,
                                                         "NATIVEH3VR_PORT");
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (!full)
            scenes = scenes.Take(1).ToArray();
        var output = Environment.GetEnvironmentVariable("NATIVEH3VR_BUILD_OUTPUT");
        if (string.IsNullOrEmpty(output))
            throw new Exception("NATIVEH3VR_BUILD_OUTPUT required");
        var report = BuildPipeline.BuildPlayer(scenes, output, BuildTarget.StandaloneOSX,
                                               BuildOptions.Development);
        Debug.Log("NATIVEH3VR_BUILD_RESULT " + report.summary.result);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
