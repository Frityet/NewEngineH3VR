// Editor-side conversion checks, copied only into the user's private project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
            public int computeShaderErrors;
            public string graphicsAPI;
            public string graphicsDevice;
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
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDevice = SystemInfo.graphicsDeviceName,
            };
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
                else if (target == BuildTarget.StandaloneOSX)
                {
                    // Unity 2021.3 documents architecture 1 as ARM64.
                    PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
                    EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture", "ARM64");
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                    PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Disabled);
                    PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                    PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Metal });
                    PlayerSettings.enableFrameTimingStats = true;
                    string workspaceId;
                    using (var hash = SHA256.Create())
                        workspaceId = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                            Path.GetFullPath(Directory.GetCurrentDirectory())))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                    PlayerSettings.companyName = "NativeH3VR-" + workspaceId;
                    PlayerSettings.productName = "NativeH3VR";
                    PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone, "local.nativeh3vr.player." + workspaceId);
                    if (PlayerSettings.GetArchitecture(BuildTargetGroup.Standalone) != 1 ||
                        PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) != ScriptingImplementation.Mono2x ||
                        !PlayerSettings.GetGraphicsAPIs(target).SequenceEqual(new[] { GraphicsDeviceType.Metal }))
                        throw new InvalidOperationException("Could not select Apple Silicon, Mono and Metal for the macOS player.");
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
                if (target == BuildTarget.StandaloneOSX)
                    CheckMetalComputeShaders(report, errors);
                var plan = Argument("-h3vrBundlePlan");
                BundleConversion.Validate(plan);
                if (build && errors.Count == 0)
                {
                    var bundleOutput = Argument("-h3vrBundleOutput");
                    bool mac = target == BuildTarget.StandaloneOSX;
                    if (!mac)
                    {
                        BundleConversion.Build(plan, bundleOutput);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    }
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    var output = Argument("-h3vrOutput");
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    var built = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, target = target, locationPathName = output, options = BuildOptions.StrictMode });
                    if (built.summary.result != BuildResult.Succeeded) errors.Add("Player build: " + built.summary.result);
                    else
                    {
                        // The macOS player reads these opaque bundles from StreamingAssets at runtime.
                        // Compile its scenes first so a scene error does not force a bundle rebuild.
                        if (mac)
                        {
                            var data = Path.Combine(output, "Contents", "Resources", "Data");
                            if (!Directory.Exists(data)) throw new DirectoryNotFoundException("The macOS player data directory is missing: " + data);
                            BundleConversion.Build(plan, bundleOutput, Path.Combine(data, "StreamingAssets"));
                        }
                        report.player = output;
                    }
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception error) { errors.Add(error.ToString()); }
            report.errors = errors.ToArray(); report.passed = errors.Count == 0;
            File.WriteAllText(Argument("-h3vrReport"), JsonUtility.ToJson(report, true));
            foreach (var error in errors) Debug.LogError(error);
            EditorApplication.Exit(report.passed ? 0 : 1);
        }

        private static void CheckMetalComputeShaders(Report report, List<string> errors)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                throw new InvalidOperationException("Compute validation needs an active Metal device.");
            foreach (var guid in AssetDatabase.FindAssets("t:ComputeShader", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                string issue = null;
                if (shader == null)
                    issue = "Compute shader failed to import";
                else if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) && IsDirect3DOnlyCompute(path))
                    issue = "Serialized Direct3D compute program requires a Steam-derived Metal source replacement";
                else if (ShaderUtil.GetComputeShaderMessages(shader).Any(message =>
                         message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                    issue = "Compute shader failed to compile";
                else if (path.EndsWith(".compute", StringComparison.OrdinalIgnoreCase))
                {
                    var kernels = Regex.Matches(File.ReadAllText(path), @"(?m)^\s*#pragma\s+kernel\s+(\w+)")
                        .Cast<Match>().Select(match => match.Groups[1].Value).ToArray();
                    if (kernels.Length == 0 || kernels.Any(kernel => !shader.HasKernel(kernel)))
                        issue = "Declared compute kernels are unavailable on Metal";
                }
                if (issue == null) continue;
                report.computeShaderErrors++;
                errors.Add(issue + ": " + path);
            }
        }

        private static bool IsDirect3DOnlyCompute(string path)
        {
            var contents = File.ReadAllText(path);
            var renderers = Regex.Matches(contents, @"(?m)^\s*-\s*targetRenderer:\s*(\d+)\s*$");
            return renderers.Count > 0 && renderers.Cast<Match>().All(row => row.Groups[1].Value == "2") &&
                   Regex.IsMatch(contents, @"(?im)^\s*code:\s*44584243");
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
