using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace NativeH3VR.GraphicsUpgrade
{
    /// <summary>Reuses the game's volume blending and LUT baker for SRP cameras.</summary>
    internal static class StockColorGrading
    {
        internal readonly struct Binding
        {
            static readonly int Lut3D = Shader.PropertyToID("_H3VRStockLut3D");
            static readonly int Lut2D = Shader.PropertyToID("_H3VRStockLut2D");
            static readonly int Parameters = Shader.PropertyToID("_H3VRStockGrade");

            internal readonly Texture Texture;
            readonly Vector4 parameters;

            internal Binding(Texture texture, int mode, float exposure)
            {
                Texture = texture;
                parameters = new Vector4(mode, exposure, 1f / texture.height, texture.height - 1f);
            }

            internal void Apply(Material material)
            {
                material.SetVector(Parameters, parameters);
                if (Texture) material.SetTexture(parameters.x == 1f ? Lut3D : Lut2D, Texture);
            }
        }

        sealed class CameraState
        {
            internal bool evaluated;
            internal int evaluatedFrame;
            internal int hash;
            internal Binding grade;
        }

        static readonly Dictionary<Camera, CameraState> Cameras = new();
        static readonly List<Camera> RemovedCameras = new();
        static StockAccess access;
        static bool initialized;
        static bool warned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            RenderPipelineManager.beginCameraRendering -= Prepare;
            access?.Release();
            access = null;
            initialized = false;
            warned = false;
            Cameras.Clear();
            RemovedCameras.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void Install()
        {
            RenderPipelineManager.beginCameraRendering -= Prepare;
            RenderPipelineManager.beginCameraRendering += Prepare;
        }

        internal static bool TryGetBinding(Camera camera, out Binding grade)
        {
            if (Cameras.TryGetValue(camera, out var state) && state.evaluatedFrame == Time.frameCount && state.grade.Texture)
            {
                grade = state.grade;
                return true;
            }
            grade = default;
            return false;
        }

        static void Prepare(ScriptableRenderContext renderContext, Camera camera)
        {
            if (!GraphicsOptions.UseStockGrading || !GraphicsSettings.currentRenderPipeline ||
                !H3VREyeRendering.IsPlayerView(camera)) return;

            try
            {
                if (!camera.TryGetComponent<PostProcessLayer>(out var layer) || !layer.isActiveAndEnabled) return;
                if (!initialized)
                {
                    initialized = true;
                    access = new StockAccess();
                }
                if (access == null) return;

                if (!Cameras.TryGetValue(camera, out var state))
                {
                    foreach (var entry in Cameras)
                        if (!entry.Key) RemovedCameras.Add(entry.Key);
                    foreach (var removed in RemovedCameras) Cameras.Remove(removed);
                    RemovedCameras.Clear();
                    state = new CameraState();
                    Cameras.Add(camera, state);
                }
                if (state.evaluated && state.evaluatedFrame == Time.frameCount) return;
                state.evaluated = true;
                state.evaluatedFrame = Time.frameCount;

                var command = CommandBufferPool.Get("Stock scene color grading");
                try
                {
                    if (access.Update(layer, command, state.hash, state.grade, out var hash, out var grade))
                        renderContext.ExecuteCommandBuffer(command);
                    state.hash = hash;
                    state.grade = grade;
                }
                catch
                {
                    state.grade = default;
                    throw;
                }
                finally { CommandBufferPool.Release(command); }
            }
            catch (Exception error)
            {
                if (warned) return;
                warned = true;
                Debug.LogWarning("Stock scene color grading is unavailable: " + error.GetBaseException().Message);
            }
        }

        sealed class StockAccess
        {
            const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;
            static readonly int StockLut3D = Shader.PropertyToID("_Lut3D");
            static readonly int StockLut2D = Shader.PropertyToID("_Lut2D");
            static readonly int StockExposure = Shader.PropertyToID("_PostExposure");

            readonly FieldInfo resources = typeof(PostProcessLayer).GetField("m_Resources", Internal);
            readonly FieldInfo contextUber = typeof(PostProcessRenderContext).GetField("uberSheet", Internal);
            readonly FieldInfo splineSamples = typeof(Spline).GetField("cachedData", Internal);

            readonly Action<PostProcessManager, PostProcessLayer> updateSettings =
                Bind<Action<PostProcessManager, PostProcessLayer>>(typeof(PostProcessManager), "UpdateSettings",
                    typeof(PostProcessLayer));
            readonly Func<PostProcessLayer, Type, PostProcessBundle> getBundle =
                Bind<Func<PostProcessLayer, Type, PostProcessBundle>>(typeof(PostProcessLayer), "GetBundle",
                    typeof(Type));
            readonly Func<PostProcessBundle, PostProcessEffectRenderer> getRenderer =
                Bind<Func<PostProcessBundle, PostProcessEffectRenderer>>(
                    typeof(PostProcessBundle).GetProperty("renderer", Internal).GetGetMethod(true));
            readonly Action<PostProcessRenderContext, PostProcessResources> setResources =
                Bind<Action<PostProcessRenderContext, PostProcessResources>>(
                    typeof(PostProcessRenderContext).GetProperty(nameof(PostProcessRenderContext.resources)).GetSetMethod(true));
            readonly Action<PostProcessRenderContext, PropertySheetFactory> setFactory =
                Bind<Action<PostProcessRenderContext, PropertySheetFactory>>(
                    typeof(PostProcessRenderContext).GetProperty(nameof(PostProcessRenderContext.propertySheets)).GetSetMethod(true));

            readonly Action<PostProcessRenderContext> beginLerp;
            readonly Action endLerp;
            readonly PostProcessRenderContext gradeContext = new();
            readonly PropertySheetFactory gradeFactory = new();

            static T Bind<T>(Type owner, string name, params Type[] parameters) where T : Delegate =>
                Bind<T>(owner.GetMethod(name, Internal, null, parameters, null));

            static T Bind<T>(MethodInfo method) where T : Delegate => (T)method.CreateDelegate(typeof(T));

            internal StockAccess()
            {
                // TextureLerper is internal in the retail game assembly; close its delegates once.
                setFactory(gradeContext, gradeFactory);
                var type = typeof(PostProcessLayer).Assembly.GetType(typeof(PostProcessLayer).Namespace + ".TextureLerper", true);
                var lerper = type.GetProperty("instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                beginLerp = (Action<PostProcessRenderContext>)type.GetMethod("BeginFrame", Internal)
                    .CreateDelegate(typeof(Action<PostProcessRenderContext>), lerper);
                endLerp = (Action)type.GetMethod("EndFrame", Internal).CreateDelegate(typeof(Action), lerper);
            }

            internal void Release() => gradeFactory.Release();

            internal bool Update(PostProcessLayer layer, CommandBuffer command, int previousHash, Binding previous,
                out int hash, out Binding result)
            {
                hash = 0;
                result = default;
                if (layer.breakBeforeColorGrading || !(resources.GetValue(layer) is PostProcessResources stockResources) ||
                    !stockResources || !stockResources.shaders.uber) return false;

                // This context is private to volume texture blending and LUT baking.
                // Its stock Reset allocates a fresh user-data dictionary unused by either.
                gradeContext.command = command;
                if (gradeContext.resources != stockResources)
                    setResources(gradeContext, stockResources);
                beginLerp(gradeContext);
                try
                {
                    updateSettings(PostProcessManager.instance, layer);
                    var bundle = getBundle(layer, typeof(ColorGrading));
                    var settings = (ColorGrading)bundle.settings;
                    if (!settings.IsEnabledAndSupported(gradeContext)) return false;

                    hash = HashCode.Combine(Hash(settings), stockResources, bundle);
                    // TextureLerper can write new blended pixels into a reused render texture.
                    // Re-evaluate such inputs even when their identity and scalar settings are unchanged.
                    bool dynamicLut = settings.gradingMode.value == GradingMode.External
                        ? settings.externalLut.value is RenderTexture
                        : settings.gradingMode.value == GradingMode.LowDefinitionRange &&
                          settings.ldrLut.value is RenderTexture;
                    if (!dynamicLut && hash == previousHash && previous.Texture &&
                        (!(previous.Texture is RenderTexture baked) || baked.IsCreated()))
                    {
                        result = previous;
                        return false;
                    }

                    var sheet = gradeFactory.Get(stockResources.shaders.uber);
                    var properties = sheet.properties;
                    properties.Clear();
                    sheet.ClearKeywords();
                    contextUber.SetValue(gradeContext, sheet);
                    getRenderer(bundle).Render(gradeContext);
                    var texture3D = properties.GetTexture(StockLut3D);
                    var texture = texture3D ? texture3D : properties.GetTexture(StockLut2D);
                    int mode = texture3D ? 1 : settings.gradingMode.value == GradingMode.LowDefinitionRange ? 3 : 2;
                    if (texture && (mode == 1 ? texture.dimension == TextureDimension.Tex3D : texture.dimension == TextureDimension.Tex2D))
                        result = new Binding(texture, mode, mode == 3 ? 1f : properties.GetFloat(StockExposure));
                    return texture;
                }
                finally
                {
                    endLerp();
                    gradeContext.command = null;
                }
            }

            int Hash(ColorGrading settings)
            {
                var hash = new HashCode();
                hash.Add((int)settings.gradingMode.value);
                hash.Add((int)settings.tonemapper.value);
                AddTexture(ref hash, settings.externalLut.value);
                AddTexture(ref hash, settings.ldrLut.value);
                hash.Add(settings.toneCurveToeStrength.value);
                hash.Add(settings.toneCurveToeLength.value);
                hash.Add(settings.toneCurveShoulderStrength.value);
                hash.Add(settings.toneCurveShoulderLength.value);
                hash.Add(settings.toneCurveShoulderAngle.value);
                hash.Add(settings.toneCurveGamma.value);
                hash.Add(settings.temperature.value);
                hash.Add(settings.tint.value);
                hash.Add(settings.colorFilter.value);
                hash.Add(settings.hueShift.value);
                hash.Add(settings.saturation.value);
                hash.Add(settings.brightness.value);
                hash.Add(settings.postExposure.value);
                hash.Add(settings.contrast.value);
                hash.Add(settings.mixerRedOutRedIn.value);
                hash.Add(settings.mixerRedOutGreenIn.value);
                hash.Add(settings.mixerRedOutBlueIn.value);
                hash.Add(settings.mixerGreenOutRedIn.value);
                hash.Add(settings.mixerGreenOutGreenIn.value);
                hash.Add(settings.mixerGreenOutBlueIn.value);
                hash.Add(settings.mixerBlueOutRedIn.value);
                hash.Add(settings.mixerBlueOutGreenIn.value);
                hash.Add(settings.mixerBlueOutBlueIn.value);
                hash.Add(settings.lift.value);
                hash.Add(settings.gamma.value);
                hash.Add(settings.gain.value);
                // The stock blend writes sampled curves. Hash their contents so in-place
                // keyframe edits and local volume weight changes invalidate the baked LUT.
                AddCurve(ref hash, settings.masterCurve.value);
                AddCurve(ref hash, settings.redCurve.value);
                AddCurve(ref hash, settings.greenCurve.value);
                AddCurve(ref hash, settings.blueCurve.value);
                AddCurve(ref hash, settings.hueVsHueCurve.value);
                AddCurve(ref hash, settings.hueVsSatCurve.value);
                AddCurve(ref hash, settings.satVsSatCurve.value);
                AddCurve(ref hash, settings.lumVsSatCurve.value);
                return hash.ToHashCode();
            }

            static void AddTexture(ref HashCode hash, Texture texture)
            {
                if (!texture) return;
                hash.Add(texture.GetEntityId());
                hash.Add(texture.updateCount);
            }

            void AddCurve(ref HashCode hash, Spline curve)
            {
                if (splineSamples.GetValue(curve) is not float[] samples) return;
                uint contents = 2166136261u;
                foreach (float sample in samples) contents = unchecked((contents ^ (uint)sample.GetHashCode()) * 16777619u);
                hash.Add(contents);
            }
        }
    }
}
