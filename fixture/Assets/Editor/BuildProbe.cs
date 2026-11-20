using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace EngineValidation
{
    public static class BuildProbe
    {
        public static void Run()
        {
            string output = Environment.GetEnvironmentVariable("UNITY_PROBE_PLAYER");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
                throw new InvalidOperationException("Set UNITY_PROBE_PLAYER to the absolute player path.");

            string platform = Environment.GetEnvironmentVariable("UNITY_PROBE_TARGET") ?? "macos";
            BuildTarget target;
            GraphicsDeviceType graphics;
            switch (platform)
            {
                case "macos" when output.EndsWith(".app", StringComparison.Ordinal):
                    target = BuildTarget.StandaloneOSX;
                    graphics = GraphicsDeviceType.Metal;
                    PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 1);
                    break;
                case "windows" when output.EndsWith(".exe", StringComparison.Ordinal):
                    target = BuildTarget.StandaloneWindows64;
                    graphics = GraphicsDeviceType.Direct3D11;
                    break;
                case "linux" when output.EndsWith(".x86_64", StringComparison.Ordinal):
                    target = BuildTarget.StandaloneLinux64;
                    graphics = GraphicsDeviceType.Vulkan;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Use macos (.app), windows (.exe), or linux (.x86_64) for UNITY_PROBE_TARGET.");
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new InvalidOperationException($"The build module for {target} is missing.");

            PlayerSettings.companyName = "Engine Validation";
            PlayerSettings.productName = "EngineProbe";
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, new[] { graphics });
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 640;
            PlayerSettings.defaultScreenHeight = 480;
            PlayerSettings.resizableWindow = false;
            PlayerSettings.runInBackground = true;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Probe probe = new GameObject("Engine probe").AddComponent<Probe>();
            probe.rasterShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Validation/Raster.shader");
            if (probe.rasterShader == null)
                throw new InvalidOperationException("The probe shader failed to import.");

            const string scenePath = "Assets/EngineProbe.generated.unity";
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new InvalidOperationException("Failed to save the generated probe scene.");

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = output,
                target = target,
                options = BuildOptions.StrictMode
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Probe build {report.summary.result}.");
        }
    }
}
