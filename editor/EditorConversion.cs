// Editor-side conversion checks, copied only into the user's private project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace H3VRConverter
{
    public static class EditorConversion
    {
        private const string TargetVersion = "2021.3.45f2";
        [Serializable] private sealed class MissingScript { public string asset; public long gameObject; public int count; }
        [Serializable] private sealed class Baseline { public MissingScript[] missingScripts; }
        [Serializable]
        private sealed class Report
        {
            public bool passed;
            public string unityVersion;
            public string target;
            public string[] errors;
            public int scenes;
            public int prefabs;
            public int originalMissingScripts;
            public int newMissingScripts;
            public int shaderErrors;
            public string player;
        }

        public static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            throw new ArgumentException("Missing " + name);
        }
        public static void Validate() { Execute(false); }
        public static void Build() { Execute(true); }

        private static void Execute(bool build)
        {
            var report = new Report { unityVersion = Application.unityVersion, target = EditorUserBuildSettings.activeBuildTarget.ToString() };
            var errors = new List<string>();
            try
            {
                if (Application.unityVersion != TargetVersion) throw new InvalidOperationException("Expected Unity " + TargetVersion + "; got " + Application.unityVersion);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                OpenXRConfiguration.Configure(EditorUserBuildSettings.activeBuildTarget);
                var target = EditorUserBuildSettings.activeBuildTarget;
                if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
                    throw new InvalidOperationException("Install the Unity player build module for " + target + ".");
                if (target == BuildTarget.StandaloneWindows64)
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                    PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
                }
                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0) throw new InvalidOperationException("No enabled build scenes. Recovery must preserve original scene order.");
                var baseline = JsonUtility.FromJson<Baseline>(File.ReadAllText("ProjectSettings/H3VRBaseline.json"));
                var expected = (baseline.missingScripts ?? new MissingScript[0]).ToDictionary(m => m.asset + "#" + m.gameObject, m => m.count);
                foreach (var path in scenes)
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    foreach (var root in scene.GetRootGameObjects()) CheckScripts(root, path, expected, report, errors);
                    report.scenes++;
                }
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) { errors.Add("Prefab did not import: " + path); continue; }
                    CheckScripts(prefab, path, expected, report, errors);
                    report.prefabs++;
                }
                foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (shader == null || ShaderUtil.ShaderHasError(shader)) { report.shaderErrors++; errors.Add("Shader failed to import or compile: " + path); }
                }
                var plan = Argument("-h3vrBundlePlan");
                BundleConversion.Validate(plan);
                if (build && errors.Count == 0)
                {
                    BundleConversion.Build(plan, Argument("-h3vrBundleOutput"));
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    var output = Argument("-h3vrOutput");
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    var built = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, target = target, locationPathName = output, options = BuildOptions.StrictMode });
                    if (built.summary.result != BuildResult.Succeeded) errors.Add("Player build: " + built.summary.result);
                    else report.player = output;
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception error) { errors.Add(error.ToString()); }
            report.errors = errors.ToArray(); report.passed = errors.Count == 0;
            File.WriteAllText(Argument("-h3vrReport"), JsonUtility.ToJson(report, true));
            foreach (var error in errors) Debug.LogError(error);
            EditorApplication.Exit(report.passed ? 0 : 1);
        }

        private static void CheckScripts(GameObject root, string asset, Dictionary<string, int> baseline, Report report, List<string> errors)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                var missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
                if (missing == 0) continue;
                var id = unchecked((long)GlobalObjectId.GetGlobalObjectIdSlow(child.gameObject).targetObjectId);
                int original;
                baseline.TryGetValue(asset + "#" + id, out original);
                report.originalMissingScripts += Math.Min(missing, original);
                if (missing <= original) continue;
                report.newMissingScripts += missing - original;
                errors.Add(asset + ": object " + id + " (" + child.name + ") acquired " + (missing - original) + " missing scripts");
            }
        }
    }
}
