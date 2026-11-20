using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Valve.VR;

namespace NativeH3VR.GraphicsUpgrade
{
    // Values after Preset index into the saved settings array. Append new choices/rows.
    public enum GraphicsSetting
    {
        Preset, RenderScale, Msaa, ShadowDistance, ShadowQuality, AdditionalLights,
        Hdr, Bloom, Sharpening, Tonemapping, AmbientOcclusion, Exposure
    }

    public static class GraphicsOptions
    {
        const string Preference = "NativeH3VR.URP.Graphics.v1";
        const string PreferenceFile = "graphics-options.json";
        const int NeutralExposureIndex = 4;
        static readonly string[] Labels =
        {
            "Preset", "Render scale", "Anti-aliasing", "Shadow distance", "Shadow detail",
            "Lights per object", "HDR rendering", "Bloom", "Sharpening", "Tone mapping", "Ambient occlusion", "Exposure"
        };
        static readonly string[][] Choices =
        {
            new[] { "Performance", "Balanced", "Quality" },
            new[] { "70%", "85%", "100%", "115%", "130%" },
            new[] { "Off", "2× MSAA", "4× MSAA" },
            new[] { "15 m", "30 m", "60 m", "100 m" },
            new[] { "Off", "Low", "Medium", "High" },
            new[] { "0", "2", "4", "8" },
            new[] { "Off", "On" }, new[] { "Off", "Subtle", "Full" },
            new[] { "Off", "Low", "Medium", "High" }, new[] { "Off", "Neutral", "ACES" },
            new[] { "Off", "On" },
            new[] { "-1 EV", "-0.75 EV", "-0.5 EV", "-0.25 EV", "0 EV", "+0.25 EV", "+0.5 EV", "+0.75 EV", "+1 EV" }
        };
        static readonly float[] RenderScales = { 0.70f, 0.85f, 1f, 1.15f, 1.30f };
        static readonly int[] Distances = { 15, 30, 60, 100 };
        static readonly int[] LightCounts = { 0, 2, 4, 8 };
        static readonly int[][] Presets =
        {
            // Scale, MSAA, distance, detail, lights, HDR, bloom, sharpen, tone, AO, exposure
            new[] { 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, NeutralExposureIndex },
            new[] { 2, 1, 1, 2, 2, 1, 1, 1, 1, 0, 5 },
            // The Neutral curve preserves more low-light detail in the game's baked interiors.
            new[] { 2, 2, 2, 3, 3, 1, 2, 1, 1, 1, 6 }
        };

        [Serializable]
        sealed class SavedOptions { public int[] values; }
        static int[] values;
        static UniversalRenderPipelineAsset source, active;
        static int lastInputFrame = -1;
        public static event Action Changed;

        public static string Label(GraphicsSetting key) => Labels[(int)key];
        public static int ChoiceCount(GraphicsSetting key) => Choices[(int)key].Length;
        public static string ChoiceLabel(GraphicsSetting key, int index) =>
            key == GraphicsSetting.Preset && index == Presets.Length ? "Custom" : Choices[(int)key][index];

        public static int GetIndex(GraphicsSetting key)
        {
            Load();
            if (key != GraphicsSetting.Preset) return values[(int)key - 1];
            for (int i = 0; i < Presets.Length; i++)
                if (values.AsSpan().SequenceEqual(Presets[i])) return i;
            return Presets.Length;
        }

        public static void SetIndex(GraphicsSetting key, int index)
        {
            if ((uint)index >= ChoiceCount(key)) throw new ArgumentOutOfRangeException(nameof(index));
            if (GetIndex(key) == index) return;
            if (key == GraphicsSetting.Preset) Array.Copy(Presets[index], values, values.Length);
            else values[(int)key - 1] = index;
            Apply();
            Save();
            Changed?.Invoke();
        }

        static void Load()
        {
            if (values != null) return;
            values = (int[])Presets[1].Clone();
            try
            {
                var stateDirectory = Environment.GetEnvironmentVariable("NATIVEH3VR_STATE_DIR");
                string json;
                if (string.IsNullOrEmpty(stateDirectory))
                {
                    if (!PlayerPrefs.HasKey(Preference)) return;
                    json = PlayerPrefs.GetString(Preference);
                }
                else
                {
                    var path = Path.Combine(stateDirectory, PreferenceFile);
                    if (!File.Exists(path)) return;
                    json = File.ReadAllText(path);
                }

                var saved = JsonUtility.FromJson<SavedOptions>(json);
                if (saved?.values == null) return;
                // Existing saves used zero compensation, including with their tone curve enabled.
                values[(int)GraphicsSetting.Exposure - 1] = NeutralExposureIndex;
                for (int i = 0; i < Math.Min(values.Length, saved.values.Length); i++)
                    if ((uint)saved.values[i] < Choices[i + 1].Length) values[i] = saved.values[i];
            }
            catch (Exception error) when (IsPreferenceError(error))
            {
                Debug.LogWarning("Could not read URP graphics preferences. Using Balanced. " + error.Message);
            }
        }

        static void Save()
        {
            string temporary = null;
            try
            {
                var json = JsonUtility.ToJson(new SavedOptions { values = values });
                var stateDirectory = Environment.GetEnvironmentVariable("NATIVEH3VR_STATE_DIR");
                if (string.IsNullOrEmpty(stateDirectory))
                {
                    PlayerPrefs.SetString(Preference, json);
                    PlayerPrefs.Save();
                    return;
                }

                Directory.CreateDirectory(stateDirectory);
                var path = Path.Combine(stateDirectory, PreferenceFile);
                temporary = Path.Combine(stateDirectory, PreferenceFile + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temporary, json);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    try { File.Move(temporary, path); }
                    catch (IOException) when (File.Exists(path)) { File.Replace(temporary, path, null); }
                }
                temporary = null;
            }
            catch (Exception error) when (IsPreferenceError(error))
            {
                Debug.LogWarning("Could not save URP graphics preferences. " + error.Message);
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (IsPreferenceError(error))
                    {
                        Debug.LogWarning("Could not remove temporary URP graphics preferences. " + error.Message);
                    }
                }
            }
        }

        static bool IsPreferenceError(Exception error) =>
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlayerPrefsException;

        internal static int Samples => 1 << GetIndex(GraphicsSetting.Msaa);
        internal static bool Hdr => GetIndex(GraphicsSetting.Hdr) != 0;
        internal static float Glow => GetIndex(GraphicsSetting.Bloom) * 0.12f;
        internal static float Sharpness => GetIndex(GraphicsSetting.Sharpening) * 0.18f;
        internal static int Tone => GetIndex(GraphicsSetting.Tonemapping);
        internal static bool Occlusion => GetIndex(GraphicsSetting.AmbientOcclusion) != 0;
        internal static float ExposureStops => (GetIndex(GraphicsSetting.Exposure) - NeutralExposureIndex) * 0.25f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            values = null;
            Changed = null;
            lastInputFrame = -1;
            source = active = null;
            RenderPipelineManager.beginCameraRendering -= PrepareCamera;
            Application.quitting -= Release;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Start()
        {
            source = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (!source) return;
            // All stock quality levels inherit this pipeline. Edit a play-session copy.
            active = UnityEngine.Object.Instantiate(source);
            active.name = source.name + " (session)";
            Apply();
            GraphicsSettings.defaultRenderPipeline = active;
            RenderPipelineManager.beginCameraRendering += PrepareCamera;
            Application.quitting += Release;
        }

        static void Apply()
        {
            if (!active) return;
            active.renderScale = RenderScales[GetIndex(GraphicsSetting.RenderScale)];
            active.msaaSampleCount = Samples;
            active.supportsHDR = Hdr;
            int shadows = GetIndex(GraphicsSetting.ShadowQuality);
            active.shadowDistance = shadows == 0 ? 0f : Distances[GetIndex(GraphicsSetting.ShadowDistance)];
            active.shadowCascadeCount = shadows == 3 ? 4 : Math.Max(1, shadows);
            active.mainLightShadowmapResolution = 512 << Math.Max(0, shadows - 1);
            active.additionalLightsShadowmapResolution = Math.Max(512, active.mainLightShadowmapResolution / 2);
            active.maxAdditionalLightsCount = LightCounts[GetIndex(GraphicsSetting.AdditionalLights)];
        }

        static void PrepareCamera(ScriptableRenderContext context, Camera camera)
        {
            // The stock pose dispatcher uses Camera.onPreCull, which SRP eyes do not raise.
            if (H3VREyeRendering.IsPlayerStereoEye(camera) && lastInputFrame != Time.frameCount)
            {
                lastInputFrame = Time.frameCount;
                SteamVR_Input.OnPreCull();
            }
            if (!H3VREyeRendering.IsPlayerView(camera)) return;
            camera.allowHDR = Hdr;
            camera.allowMSAA = Samples > 1;
        }

        static void Release()
        {
            RenderPipelineManager.beginCameraRendering -= PrepareCamera;
            if (GraphicsSettings.defaultRenderPipeline == active) GraphicsSettings.defaultRenderPipeline = source;
            UnityEngine.Object.Destroy(active);
            source = active = null;
            H3VREyeRendering.Release();
        }
    }
}
