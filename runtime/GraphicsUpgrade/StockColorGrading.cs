using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

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
                if (!initialized)
                {
                    initialized = true;
                    access = StockAccess.Find();
                }
                if (access == null) return;

                var layer = camera.GetComponent(access.LayerType) as Behaviour;
                if (!layer || !layer.isActiveAndEnabled) return;

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

        // Retail post-processing classes are defined in the imported game assembly.
        // Resolve its runtime API once so the optional renderer has no dependency on a mod assembly.
        sealed class StockAccess
        {
            const string Prefix = "UnityEngine.Rendering.PostProcessing.";
            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            static readonly int StockLut3D = Shader.PropertyToID("_Lut3D");
            static readonly int StockLut2D = Shader.PropertyToID("_Lut2D");
            static readonly int StockExposure = Shader.PropertyToID("_PostExposure");

            internal readonly Type LayerType;
            readonly Type gradingType;
            readonly PropertyInfo managerInstance, textureLerperInstance, bundleSettings, bundleRenderer;
            readonly PropertyInfo contextCommand, contextResources, contextFactory, sheetProperties;
            readonly MethodInfo updateSettings, getBundle, contextReset, getSheet, beginLerp, endLerp, renderGrade, releaseSheets;
            readonly FieldInfo resources, resourceShaders, uberShader, contextUber, effectEnabled, gradingMode;
            readonly (FieldInfo parameter, FieldInfo value)[] values;
            readonly FieldInfo splineSamples;
            readonly object gradeContext, gradeFactory;

            internal static StockAccess Find()
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetType(Prefix + "PostProcessLayer") is Type layer)
                        return new StockAccess(layer);
                return null;
            }

            StockAccess(Type layer)
            {
                LayerType = layer;
                var assembly = layer.Assembly;
                Type Get(string name) => assembly.GetType(Prefix + name, true);
                gradingType = Get("ColorGrading");
                var manager = Get("PostProcessManager");
                var lerper = Get("TextureLerper");
                var bundle = Get("PostProcessBundle");
                var context = Get("PostProcessRenderContext");
                var sheetFactory = Get("PropertySheetFactory");
                managerInstance = manager.GetProperty("instance", Static);
                textureLerperInstance = lerper.GetProperty("instance", Static);
                bundleSettings = bundle.GetProperty("settings", Instance);
                bundleRenderer = bundle.GetProperty("renderer", Instance);
                contextCommand = context.GetProperty("command", Instance);
                contextResources = context.GetProperty("resources", Instance);
                contextFactory = context.GetProperty("propertySheets", Instance);
                sheetProperties = Get("PropertySheet").GetProperty("properties", Instance);
                updateSettings = manager.GetMethod("UpdateSettings", Instance);
                getBundle = layer.GetMethod("GetBundle", Instance, null, new[] { typeof(Type) }, null);
                contextReset = context.GetMethod("Reset", Instance);
                getSheet = sheetFactory.GetMethod("Get", Instance, null, new[] { typeof(Shader) }, null);
                releaseSheets = sheetFactory.GetMethod("Release", Instance);
                beginLerp = lerper.GetMethod("BeginFrame", Instance);
                endLerp = lerper.GetMethod("EndFrame", Instance);
                renderGrade = Get("ColorGradingRenderer").GetMethod("Render", Instance);
                resources = layer.GetField("m_Resources", Instance);
                resourceShaders = Get("PostProcessResources").GetField("shaders", Instance);
                uberShader = resourceShaders.FieldType.GetField("uber", Instance);
                contextUber = context.GetField("uberSheet", Instance);
                effectEnabled = gradingType.GetField("enabled", Instance);
                gradingMode = gradingType.GetField("gradingMode", Instance);
                splineSamples = Get("Spline").GetField("cachedData", Instance);
                var fields = new List<(FieldInfo, FieldInfo)>();
                foreach (var parameter in gradingType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                    if (parameter.FieldType.GetField("value", Instance) is FieldInfo value) fields.Add((parameter, value));
                values = fields.ToArray();
                gradeContext = Activator.CreateInstance(context);
                gradeFactory = Activator.CreateInstance(sheetFactory);
            }

            internal void Release() => releaseSheets.Invoke(gradeFactory, null);

            internal bool Update(Behaviour layer, CommandBuffer command, int previousHash, Binding previous,
                out int hash, out Binding result)
            {
                hash = 0;
                result = default;
                var stockResources = resources.GetValue(layer) as UnityEngine.Object;
                if (!stockResources) return false;

                var shader = uberShader.GetValue(resourceShaders.GetValue(stockResources)) as Shader;
                if (!shader) return false;
                var sheet = getSheet.Invoke(gradeFactory, new object[] { shader });
                var properties = (MaterialPropertyBlock)sheetProperties.GetValue(sheet);
                // Use the stock context only to blend grading and bake its lookup texture.
                contextReset.Invoke(gradeContext, null);
                contextCommand.SetValue(gradeContext, command);
                contextResources.SetValue(gradeContext, stockResources);
                contextFactory.SetValue(gradeContext, gradeFactory);
                contextUber.SetValue(gradeContext, sheet);

                var lerper = textureLerperInstance.GetValue(null);
                beginLerp.Invoke(lerper, new[] { gradeContext });
                try
                {
                    updateSettings.Invoke(managerInstance.GetValue(null), new object[] { layer });
                    var bundle = getBundle.Invoke(layer, new object[] { gradingType });
                    var settings = bundleSettings.GetValue(bundle);
                    if (!(bool)effectEnabled.FieldType.GetField("value", Instance).GetValue(effectEnabled.GetValue(settings)))
                        return false;

                    hash = Hash(settings);
                    if (hash == previousHash && previous.Texture &&
                        (!(previous.Texture is RenderTexture baked) || baked.IsCreated()))
                    {
                        result = previous;
                        return false;
                    }

                    properties.Clear();
                    renderGrade.Invoke(bundleRenderer.GetValue(bundle), new[] { gradeContext });
                    var texture3D = properties.GetTexture(StockLut3D);
                    var texture2D = properties.GetTexture(StockLut2D);
                    int mode = texture3D ? 1 : Convert.ToInt32(gradingMode.FieldType.GetField("value", Instance)
                        .GetValue(gradingMode.GetValue(settings))) == 0 ? 3 : 2;
                    var texture = texture3D ? texture3D : texture2D;
                    if (texture && (mode == 1 ? texture.dimension == TextureDimension.Tex3D : texture.dimension == TextureDimension.Tex2D))
                        result = new Binding(texture, mode, mode == 3 ? 1f : properties.GetFloat(StockExposure));
                    return texture;
                }
                finally { endLerp.Invoke(lerper, null); }
            }

            int Hash(object settings)
            {
                var hash = new HashCode();
                foreach (var (parameter, value) in values)
                {
                    var item = value.GetValue(parameter.GetValue(settings));
                    if (item != null && item.GetType() == splineSamples.DeclaringType)
                    {
                        if (splineSamples.GetValue(item) is float[] samples)
                            foreach (var sample in samples) hash.Add(sample);
                    }
                    else hash.Add(item);
                }
                return hash.ToHashCode();
            }
        }
    }
}
