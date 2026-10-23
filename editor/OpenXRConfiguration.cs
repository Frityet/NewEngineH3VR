using System;
using System.IO;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace H3VRConverter
{
    public static class OpenXRConfiguration
    {
        [Serializable]
        private sealed class ProviderConfiguration
        {
            public string loader;
        }

        public static void Configure(BuildTarget target)
        {
            var openXRPackage = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(OpenXRSettings).Assembly);
            if (openXRPackage == null || openXRPackage.version != "1.13.0")
                throw new InvalidOperationException("Expected com.unity.xr.openxr 1.13.0. Restore the converter's pinned package manifest.");
            var group = BuildPipeline.GetBuildTargetGroup(target);
            const string stockLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
            var loader = stockLoader;
            const string providerPath = "ProjectSettings/H3VRProvider.json";
            if (File.Exists(providerPath))
                loader = JsonUtility.FromJson<ProviderConfiguration>(File.ReadAllText(providerPath)).loader;
            if (string.IsNullOrWhiteSpace(loader))
                throw new InvalidOperationException("The platform integration did not specify a Unity XR loader type.");
            if (loader == stockLoader && target != BuildTarget.StandaloneWindows64)
                throw new NotSupportedException("The pinned stock OpenXR provider supports Windows64 for this converter. This target requires a compatible Unity XR display/input provider backed by OpenXR.");

            XRGeneralSettingsPerBuildTarget config;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out config) || config == null)
            {
                config = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                Directory.CreateDirectory("Assets/XR/Settings");
                AssetDatabase.Refresh();
                AssetDatabase.CreateAsset(config, "Assets/XR/Settings/XRGeneralSettings.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, config, true);
            }
            if (!config.HasSettingsForBuildTarget(group)) config.CreateDefaultSettingsForBuildTarget(group);
            if (!config.HasManagerSettingsForBuildTarget(group)) config.CreateDefaultManagerSettingsForBuildTarget(group);
            var settings = config.SettingsForBuildTarget(group);
            settings.InitManagerOnStart = true;
            var manager = settings.Manager;
            manager.automaticLoading = true;
            manager.automaticRunning = true;
            // Exactly one provider prevents fallback to the original OpenVR backend.
            manager.TrySetLoaders(new System.Collections.Generic.List<XRLoader>());
            if (!XRPackageMetadataStore.AssignLoader(manager, loader, group) || manager.activeLoaders.Count != 1 || manager.activeLoaders[0].GetType().FullName != loader)
                throw new InvalidOperationException("Could not assign the requested OpenXR-backed Unity XR loader: " + loader);

            if (loader == stockLoader)
            {
                FeatureHelpers.RefreshFeatures(group);
                var xr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if (xr == null) throw new InvalidOperationException("OpenXR settings were not created.");
                // The original optics and post effects use separate eye rendering.
                xr.renderMode = OpenXRSettings.RenderMode.MultiPass;
                var sessionEnabled = false;
                foreach (var feature in xr.GetFeatures<OpenXRFeature>())
                {
                    // Optional extension features can require unsupported capabilities.
                    var isController = feature.GetType().Namespace == "UnityEngine.XR.OpenXR.Features.Interactions"
                        && feature.GetType().Name.EndsWith("ControllerProfile", StringComparison.Ordinal);
                    var isSession = feature.GetType().Name == "OpenXRSessionFeature";
                    if (isController || isSession)
                    {
                        feature.enabled = true;
                        EditorUtility.SetDirty(feature);
                        sessionEnabled |= isSession;
                    }
                }
                if (!sessionEnabled) throw new InvalidOperationException("The OpenXR session focus feature is missing.");
                EditorUtility.SetDirty(xr);
            }
            EditorUtility.SetDirty(config);
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(manager);
            AssetDatabase.SaveAssets();
        }
    }
}
