#if NATIVEH3VR_GRAPHICS_UPGRADE
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NativeH3VR.GraphicsUpgrade;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace H3VRConverter
{
    public static class GraphicsUpgradeConfiguration
    {
        private const string PackageName = "com.unity.render-pipelines.universal";
        private const string PackageVersion = "17.7.0";
        private const string Assets = "Assets/NativeH3VR/GraphicsUpgrade";
        private const string Generated = Assets + "/Generated";
        private const string PipelinePath = Generated + "/NativeH3VRRenderPipeline.asset";
        private const string RendererPath = Generated + "/NativeH3VRRenderer.asset";
        private const string ShaderPath = Assets + "/Shaders/GameClarity.shader";
        private const string FeedbackShader = Assets + "/Shaders/StockTransientFeedback.shader";
        private const string TrailShader = Assets + "/Shaders/StockProceduralTrail.shader";
        private const string Marker = "ProjectSettings/NativeH3VRGraphicsUpgrade.json";

        [Serializable]
        private sealed class SetupReport
        {
            public bool passed;
            public string error;
            public string unityVersion;
            public string packageVersion;
            public string pipelineAsset;
            public string rendererAsset;
        }

        public static void Prepare()
        {
            var report = new SetupReport
            {
                unityVersion = Application.unityVersion,
                packageVersion = PackageVersion,
                pipelineAsset = PipelinePath,
                rendererAsset = RendererPath
            };
            try
            {
                if (EditorConversion.Argument("-nativeH3vrUnityVersion") != Application.unityVersion)
                    throw new InvalidOperationException("The graphics upgrade setup launched a different Unity editor than requested.");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                VerifyPackage();
                GraphicsUpgradeThermalShaders.Prepare();
                GraphicsUpgradeThermalShaders.PrepareStockPbr();
                ConfigurePipeline();
                GraphicsUpgradeMaterials.Apply();
                AssetDatabase.SaveAssets();
                VerifyPipeline();
                report.passed = true;
                File.WriteAllText(Marker, JsonUtility.ToJson(report, true));
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                Debug.LogError(report.error);
            }
            File.WriteAllText(EditorConversion.Argument("-h3vrReport"), JsonUtility.ToJson(report, true));
            EditorApplication.Exit(report.passed ? 0 : 1);
        }

        private static void ConfigurePipeline()
        {
            if (!AssetDatabase.IsValidFolder(Generated))
                AssetDatabase.CreateFolder(Assets, "Generated");
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.name = "NativeH3VR Renderer";
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }
            if (renderer.postProcessData == null)
                renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                    "Packages/" + PackageName + "/Runtime/Data/PostProcessData.asset");
            ResourceReloader.ReloadAllNullIn(renderer, "Packages/" + PackageName);
            if (renderer.postProcessData == null || renderer.postProcessData.shaders?.temporalAntialiasingPS == null)
                throw new InvalidOperationException("Could not load the URP temporal antialiasing resources.");
            renderer.renderingMode = RenderingMode.Forward;
            var feature = EnsureFeature<H3VRRendererFeature>(renderer, "NativeH3VR Game Clarity");
            feature.compositionShader = LoadShader(ShaderPath);
            EditorUtility.SetDirty(feature);

            EnsureFeature<H3VRLegacyLighting>(renderer, "NativeH3VR Legacy Lighting");
            var thermal = EnsureFeature<H3VRThermalScope>(renderer, "NativeH3VR Thermal Scope");
            thermal.opaque = LoadShader(GraphicsUpgradeThermalShaders.PathFor("Opaque"));
            thermal.cutout = LoadShader(GraphicsUpgradeThermalShaders.PathFor("TransparentCutout"));
            thermal.transparent = LoadShader(GraphicsUpgradeThermalShaders.PathFor("Transparent"));
            EditorUtility.SetDirty(thermal);
            var effects = EnsureFeature<H3VRStockEffects>(renderer, "NativeH3VR Stock Effects");
            effects.screenShakeShader = LoadShader(FeedbackShader);
            effects.trailShader = LoadShader(TrailShader);
            EditorUtility.SetDirty(effects);
            var occlusion = EnsureFeature<H3VRAmbientOcclusion>(renderer, "NativeH3VR Ambient Occlusion");
            var occlusionSettings = new SerializedObject(occlusion);
            var ao = occlusionSettings.FindProperty("m_Settings");
            ao.FindPropertyRelative("AOMethod").intValue = 1;
            ao.FindPropertyRelative("Source").intValue = 0;
            ao.FindPropertyRelative("NormalSamples").intValue = 1;
            ao.FindPropertyRelative("Samples").intValue = 2;
            ao.FindPropertyRelative("AfterOpaque").boolValue = true;
            ao.FindPropertyRelative("Downsample").boolValue = true;
            occlusionSettings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(occlusion);
            var rendererSettings = new SerializedObject(renderer);
            var featureMap = rendererSettings.FindProperty("m_RendererFeatureMap");
            featureMap.arraySize = renderer.rendererFeatures.Count;
            for (int i = 0; i < renderer.rendererFeatures.Count; i++)
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out string _, out long localId))
                    throw new InvalidOperationException("Could not serialize the NativeH3VR renderer feature.");
                featureMap.GetArrayElementAtIndex(i).longValue = localId;
            }
            rendererSettings.ApplyModifiedPropertiesWithoutUndo();
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.name = "NativeH3VR Render Pipeline";
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }
            pipeline.supportsHDR = true;
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            pipeline.useSRPBatcher = true;
            pipeline.msaaSampleCount = 4;
            pipeline.shadowCascadeCount = 4;
            var pipelineSettings = new SerializedObject(pipeline);
            pipelineSettings.FindProperty("m_MainLightRenderingMode").intValue = (int)LightRenderingMode.PerPixel;
            pipelineSettings.FindProperty("m_AdditionalLightsRenderingMode").intValue = (int)LightRenderingMode.PerPixel;
            pipelineSettings.FindProperty("m_MainLightShadowsSupported").boolValue = true;
            pipelineSettings.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
            pipelineSettings.FindProperty("m_AnyShadowsSupported").boolValue = true;
            pipelineSettings.FindProperty("m_SoftShadowsSupported").boolValue = true;
            pipelineSettings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);

            // The migrated project keeps its original quality tiers. Give each tier
            // the same default pipeline so game-driven quality changes preserve URP.
            var qualityAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset").Single();
            var quality = new SerializedObject(qualityAsset);
            var levels = quality.FindProperty("m_QualitySettings");
            for (int i = 0; i < levels.arraySize; i++)
                levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline").objectReferenceValue = null;
            quality.ApplyModifiedPropertiesWithoutUndo();
            GraphicsSettings.defaultRenderPipeline = pipeline;
        }

        private static Shader LoadShader(string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("Could not import the graphics upgrade shader: " + path);
            return shader;
        }

        private static T EnsureFeature<T>(UniversalRendererData renderer, string name) where T : ScriptableRendererFeature
        {
            var feature = AssetDatabase.LoadAllAssetsAtPath(RendererPath).OfType<T>().SingleOrDefault();
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<T>();
                var script = MonoScript.FromScriptableObject(feature);
                if (script == null || script.GetClass() != typeof(T))
                    throw new InvalidOperationException("Unity cannot serialize the renderer feature " + typeof(T).FullName + ".");
                feature.name = name;
                AssetDatabase.AddObjectToAsset(feature, renderer);
            }
            feature.SetActive(true);
            if (!renderer.rendererFeatures.Contains(feature))
                renderer.rendererFeatures.Add(feature);
            return feature;
        }

        internal static void KeepRuntimeShaderVariants()
        {
            // URP 17.7 uses transient renderer policy to strip off variants during
            // shader preprocessing. Keep those variants for in-game quality changes.
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            foreach (var name in new[] { "stripShadowsOffVariants", "stripAdditionalLightOffVariants" })
            {
                var property = typeof(UniversalRendererData).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
                    throw new InvalidOperationException("Could not configure the URP shader variant policy: " + name);
                property.SetValue(renderer, false);
            }
        }

        private static void VerifyPackage()
        {
            var package = PackageInfo.FindForAssetPath("Packages/" + PackageName + "/package.json");
            if (package == null || package.version != PackageVersion)
                throw new InvalidOperationException("Expected " + PackageName + "@" + PackageVersion + ".");
        }

        internal static void VerifyPipeline()
        {
            VerifyPackage();
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (pipeline == null || GraphicsSettings.defaultRenderPipeline != pipeline ||
                GraphicsSettings.currentRenderPipeline != pipeline || renderer == null ||
                !pipeline.rendererDataList.ToArray().Contains(renderer) ||
                !renderer.rendererFeatures.OfType<H3VRRendererFeature>().Any(feature =>
                    feature.isActive && AssetDatabase.GetAssetPath(feature.compositionShader) == ShaderPath) ||
                !renderer.rendererFeatures.OfType<H3VRLegacyLighting>().Any(feature => feature.isActive) ||
                !renderer.rendererFeatures.OfType<H3VRThermalScope>().Any(feature => feature.isActive &&
                    AssetDatabase.GetAssetPath(feature.opaque) == GraphicsUpgradeThermalShaders.PathFor("Opaque") &&
                    AssetDatabase.GetAssetPath(feature.cutout) == GraphicsUpgradeThermalShaders.PathFor("TransparentCutout") &&
                    AssetDatabase.GetAssetPath(feature.transparent) == GraphicsUpgradeThermalShaders.PathFor("Transparent")) ||
                !renderer.rendererFeatures.OfType<H3VRStockEffects>().Any(feature => feature.isActive &&
                    AssetDatabase.GetAssetPath(feature.screenShakeShader) == FeedbackShader &&
                    AssetDatabase.GetAssetPath(feature.trailShader) == TrailShader) ||
                !renderer.rendererFeatures.OfType<H3VRAmbientOcclusion>().Any(feature => feature.isActive) ||
                Enumerable.Range(0, QualitySettings.names.Length).Any(i => QualitySettings.GetRenderPipelineAssetAt(i) != null))
                throw new InvalidOperationException("The NativeH3VR graphics upgrade pipeline or renderer feature is not active.");
        }
    }
}
#endif
