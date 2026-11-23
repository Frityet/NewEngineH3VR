using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace NativeH3VR.QA
{
    internal sealed class GraphicsPresetControls
    {
        private const string AssemblySuffix = ", NativeH3VR.GraphicsUpgrade";
        private const string Namespace = "NativeH3VR.GraphicsUpgrade.";
        private const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly Type options = Type.GetType(Namespace + "GraphicsOptions" + AssemblySuffix, true);
        private readonly Type setting = Type.GetType(Namespace + "GraphicsSetting" + AssemblySuffix, true);

        internal IEnumerable<string> Keys => Enum.GetNames(setting);
        private object Key(string name) => Enum.Parse(setting, name);
        internal int Count(string name) => (int)options.GetMethod("ChoiceCount").Invoke(null, new[] { Key(name) });
        internal int Index(string name) => (int)options.GetMethod("GetIndex").Invoke(null, new[] { Key(name) });
        internal void Set(string name, int index) => options.GetMethod("SetIndex").Invoke(null, new[] { Key(name), (object)index });
        internal string Label(string name) => (string)options.GetMethod("ChoiceLabel").Invoke(null, new[] { Key(name), (object)Index(name) });

        internal static (int reflections, int fog, int receivers) ReadEffects()
        {
            var effects = Type.GetType(Namespace + "H3VRScreenEffects" + AssemblySuffix, true);
            int Read(string name) => (int)effects.GetProperty(name, InternalStatic).GetValue(null);
            return (Read("ReflectionTraces"), Read("FogIntegrations"), Read("LastReceiverCount"));
        }

        internal static void CheckUrp(int preset, NativeStereoRenderer stereo, string report, string expectedSize,
            (int reflections, int fog, int receivers) before, int[] previousHistoryVersions,
            float? expectedRenderScale = null, int? expectedSamples = null)
        {
            var options = new GraphicsPresetControls();
            var asset = GraphicsSettings.currentRenderPipeline;
            Require(asset && asset.GetType().FullName == "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset",
                "Active graphics pipeline is not URP");
            object Property(string key) => asset.GetType().GetProperty(key).GetValue(asset);
            float scale = (float)Property("renderScale");
            int samples = (int)Property("msaaSampleCount");
            float distance = (float)Property("shadowDistance");
            int shadows = (int)Property("mainLightShadowmapResolution");
            int additionalShadows = (int)Property("additionalLightsShadowmapResolution");
            int cascades = (int)Property("shadowCascadeCount");
            int shadowChoices = options.Count("ShadowQuality");
            int lights = (int)Property("maxAdditionalLightsCount");
            bool hdr = (bool)Property("supportsHDR");
            int aa = options.Index("Msaa");
            int fog = options.Index("FogQuality");
            int reflections = options.Index("Reflections");
            int characterShadows = options.Index("CharacterShadows");
            Require(options.Count("CharacterShadows") == 2, "Expected Scene and Enhanced character shadows");

            Require(!expectedRenderScale.HasValue || Mathf.Approximately(scale, expectedRenderScale.Value),
                $"The active URP render scale is {scale:R}, expected {expectedRenderScale:R}");
            Require(!expectedSamples.HasValue || samples == expectedSamples.Value,
                $"The active URP MSAA sample count is {samples}, expected {expectedSamples}");
            Require(options.Index("Preset") == preset, "The active graphics preset did not match " + preset);
            Require(shadowChoices is 4 or 5, "Unexpected URP shadow detail choices");
            bool expandedUltra = preset == 2 && shadowChoices == 5;
            if (preset < 3)
            {
                float expectedDistance = expandedUltra ? 100f : preset == 0 ? 30f : 60f;
                int expectedShadows = expandedUltra ? 4096 : preset == 0 ? 1024 : 2048;
                Require(options.Index("RenderScale") == 2 && Mathf.Approximately(scale, 1f) &&
                        aa == preset + 1 && samples == new[] { 2, 4, 1 }[preset] &&
                        Mathf.Approximately(distance, expectedDistance) &&
                        shadows == expectedShadows && additionalShadows == expectedShadows / 2 &&
                        cascades == (preset == 0 ? 2 : 4) && lights == (preset == 0 ? 4 : 8) && hdr &&
                        fog == preset && reflections == (preset == 2 ? 1 : 0) && characterShadows == (preset == 2 ? 1 : 0),
                    "URP did not apply the requested full-size preset");
            }

            Require(stereo && stereo.EyeWidth > 0 && stereo.EyeHeight > 0, "URP stereo outputs are unavailable");
            string size = $"{stereo.EyeWidth} {stereo.EyeHeight}";
            Require(string.IsNullOrEmpty(expectedSize) || size == expectedSize,
                $"URP stereo size is {size}, expected {expectedSize}");
            var lines = new List<string>
            {
                asset.name, "preset=" + options.Label("Preset"), "antiAliasing=" + options.Label("Msaa"),
                FormattableString.Invariant($"renderScale={scale:R}"), "msaaSampleCount=" + samples,
                FormattableString.Invariant($"shadowDistance={distance:R}"), "mainLightShadowmapResolution=" + shadows,
                "additionalLightsShadowmapResolution=" + additionalShadows, "shadowCascadeCount=" + cascades,
                "shadowDetailChoices=" + shadowChoices, "maxAdditionalLightsCount=" + lights, "supportsHDR=" + hdr, "eyeSize=" + size,
                "fog=" + options.Label("FogQuality"), "sceneFog=" + RenderSettings.fog,
                "reflections=" + options.Label("Reflections"), "characterShadows=" + options.Label("CharacterShadows")
            };
            for (int eye = 0; eye < 2; eye++)
            {
                var texture = stereo.EyeTexture(eye);
                Require(texture && texture.IsCreated() && texture.width == stereo.EyeWidth && texture.height == stereo.EyeHeight,
                    $"URP eye {eye} is missing its full-size submitted target");
            }
            if (aa == 3)
            {
                string expectedQuality = preset == 2 ? (expandedUltra ? "High" : "Medium") : null;
                var versions = ReadTemporalHistories(lines, expectedQuality);
                Require(previousHistoryVersions != null && versions[0] > previousHistoryVersions[0] &&
                        versions[1] > previousHistoryVersions[1],
                    "The native TAA histories did not advance during the witness");
            }

            var after = ReadEffects();
            int traces = after.reflections - before.reflections;
            int integration = after.fog - before.fog;
            Require(reflections != 0 ? after.receivers == 0 || traces > 0 : traces == 0,
                "Screen-space reflection passes did not match the setting and visible receivers");
            bool authoredFog = fog != 0 && RenderSettings.fog;
            Require(authoredFog || integration == 0, "Fog integration ran with fog disabled in the scene or settings");
            bool shadowedSun = RenderSettings.sun && RenderSettings.sun.isActiveAndEnabled &&
                RenderSettings.sun.type == LightType.Directional && RenderSettings.sun.shadows != LightShadows.None;
            Require(!authoredFog || !shadowedSun || integration > 0, "Authored fog and sun shadows did not run the fog integration");
            lines.Add($"reflectionTraces={traces} reflectionReceivers={after.receivers} fogIntegrations={integration} shadowedSun={shadowedSun}");
            File.WriteAllLines(report, lines);
        }

        internal static int[] ReadTemporalHistories(List<string> lines = null, string expectedQuality = null)
        {
            var urp = GraphicsSettings.currentRenderPipeline.GetType().Assembly;
            string[] names = { "NativeH3VR URP Left Eye", "NativeH3VR URP Right Eye" };
            var cameraDataType = urp.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData", true);
            var historyType = urp.GetType("UnityEngine.Rendering.Universal.TaaHistory", true);
            var cameras = Resources.FindObjectsOfTypeAll<Camera>();
            object[] histories = new object[2];
            RenderTexture[] accumulation = new RenderTexture[2];
            int[] versions = new int[2];
            for (int eye = 0; eye < 2; eye++)
            {
                var camera = cameras.SingleOrDefault(candidate => candidate.name == names[eye]);
                var data = camera ? camera.GetComponent(cameraDataType) : null;
                Require(data &&
                        cameraDataType.GetProperty("antialiasing").GetValue(data).ToString() == "TemporalAntiAliasing" &&
                        (bool)cameraDataType.GetProperty("renderPostProcessing").GetValue(data) &&
                        !(bool)cameraDataType.GetProperty("allowXRRendering").GetValue(data),
                    $"URP eye {eye} did not enable native per-eye temporal AA");
                var settings = cameraDataType.GetField("m_TaaSettings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(data);
                string quality = settings.GetType().GetProperty("quality").GetValue(settings).ToString();
                Require(expectedQuality == null || quality == expectedQuality,
                    $"URP eye {eye} TAA quality is {quality}, expected {expectedQuality}");
                lines?.Add($"temporalQuality{eye}={quality}");
                var history = ReadTemporalHistory(data, historyType);
                Require(history != null, $"URP eye {eye} has no written TAA history");
                histories[eye] = history;
                var handle = historyType.GetMethod("GetAccumulationTexture").Invoke(history, new object[] { 0 });
                accumulation[eye] = handle?.GetType().GetProperty("rt").GetValue(handle) as RenderTexture;
                int version = (int)historyType.GetMethod("GetAccumulationVersion").Invoke(history, new object[] { 0 });
                Require(accumulation[eye] && accumulation[eye].IsCreated() && version >= 0,
                    $"URP eye {eye} has no current TAA accumulation target");
                versions[eye] = version;
                lines?.Add($"temporalEye{eye}={camera.GetEntityId()} accumulation={accumulation[eye].GetEntityId()} frame={version}");
            }
            Require(!ReferenceEquals(histories[0], histories[1]) && accumulation[0] != accumulation[1],
                "Both URP eyes shared the same TAA history");
            Require(versions[0] == versions[1], "The URP stereo histories were written on different engine frames");
            return versions;
        }

        private static object ReadTemporalHistory(Component data, Type historyType)
        {
            var access = data.GetType().GetProperty("history").GetValue(data);
            return access.GetType().GetMethod("GetHistoryForRead").MakeGenericMethod(historyType).Invoke(access, null);
        }

        internal static void CheckUnrelatedRenderRequest(string report)
        {
            var urp = GraphicsSettings.currentRenderPipeline.GetType().Assembly;
            var dataType = urp.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData", true);
            var historyType = urp.GetType("UnityEngine.Rendering.Universal.TaaHistory", true);
            var requestType = urp.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipeline+SingleCameraRequest", true);
            var owner = new GameObject("Independent graphics witness camera");
            var target = new RenderTexture(64, 64, 24);
            var resized = new RenderTexture(80, 96, 24);
            try
            {
                var camera = owner.AddComponent<Camera>();
                camera.enabled = false;
                camera.cullingMask = 0;
                camera.allowMSAA = false;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                var data = owner.AddComponent(dataType);
                dataType.GetProperty("allowXRRendering").SetValue(data, false);
                dataType.GetProperty("renderPostProcessing").SetValue(data, true);
                var aa = dataType.GetProperty("antialiasing");
                aa.SetValue(data, Enum.Parse(aa.PropertyType, "TemporalAntiAliasing"));
                var request = Activator.CreateInstance(requestType);
                requestType.GetField("destination").SetValue(request, target);
                var submit = typeof(RenderPipeline).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(method => method.Name == "SubmitRenderRequest" && method.IsGenericMethodDefinition);
                var submitCamera = submit.MakeGenericMethod(requestType);
                var nativeBefore = ReadTemporalHistories();
                submitCamera.Invoke(null, new[] { (object)camera, request });
                var history = ReadTemporalHistory(data, historyType);
                var version = historyType.GetMethod("GetAccumulationVersion");
                var texture = historyType.GetMethod("GetAccumulationTexture");
                var firstTexture = history == null ? null : texture.Invoke(history, new object[] { 0 });
                int firstVersion = history == null ? 0 : (int)version.Invoke(history, new object[] { 0 });
                // URP can allocate on a transient request. A brand-new history starts at 0,
                // Reset marks it -1, and a real accumulation records the positive frame index.
                Require(Time.frameCount > 0 && firstVersion <= 0,
                    $"An unrelated one-shot render request wrote TAA history at frame {firstVersion}");
                requestType.GetField("destination").SetValue(request, resized);
                submitCamera.Invoke(null, new[] { (object)camera, request });
                var current = ReadTemporalHistory(data, historyType);
                int currentVersion = current == null ? 0 : (int)version.Invoke(current, new object[] { 0 });
                var currentTexture = current == null ? null : texture.Invoke(current, new object[] { 0 });
                Require(currentVersion == firstVersion && ReferenceEquals(firstTexture, currentTexture),
                    "Resizing an unrelated one-shot target changed its TAA accumulation");
                var nativeAfter = ReadTemporalHistories();
                Require(nativeBefore.SequenceEqual(nativeAfter), "An unrelated one-shot advanced the native stereo histories");
                File.WriteAllLines(report, new[]
                {
                    $"stillVersion={firstVersion} resizedVersion={currentVersion} stillAllocated={firstTexture != null}",
                    $"nativeBefore={string.Join(",", nativeBefore)} nativeAfter={string.Join(",", nativeAfter)}"
                });
            }
            finally
            {
                target.Release();
                resized.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(resized);
                UnityEngine.Object.Destroy(owner);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
