using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NativeH3VR.GraphicsUpgrade
{
    internal sealed class H3VRScreenEffects : ScriptableRenderPass
    {
        const uint ReceiverLayer = 1u << 31;
        const int DenseReceiverThreshold = 384;
        const int DenseReceiverLimit = 64;
        const float DenseRangeSquared = 22f * 22f;
        const string BundleName = "nativeh3vr-urp-screen-effects";
        const string ShaderName = "Hidden/NativeH3VR/URP Screen Effects";
        static readonly int Effect = Shader.PropertyToID("_H3VRScreenEffect");
        static readonly int Fog = Shader.PropertyToID("_H3VRScreenFog");
        static readonly int Sun = Shader.PropertyToID("_H3VRScreenSun");
        static readonly int Mask = Shader.PropertyToID("_H3VRReflectionMask");
        static readonly int Ssr = Shader.PropertyToID("_H3VRReflections");
        static readonly int Shafts = Shader.PropertyToID("_H3VRShafts");
        static readonly int MainShadows = Shader.PropertyToID("_MainLightShadowmapTexture");
        static readonly Vector4 Identity = new(1, 1, 0, 0);
        static readonly List<ShaderTagId> OpaqueTags = new()
        {
            new("UniversalForward"), new("UniversalForwardOnly"), new("SRPDefaultUnlit")
        };

        internal static int ReflectionTraces { get; private set; }
        internal static int FogIntegrations { get; private set; }
        internal static int LastReceiverCount { get; private set; }

        readonly List<Renderer> receivers = new();
        readonly List<Receiver> candidates = new();
        readonly List<RankedReceiver> shortlist = new();
        readonly HashSet<Renderer> selected = new();
        readonly List<Material> surfaces = new();
        readonly Plane[] planes = new Plane[6];
        Material material;
        Scene markedScene;
        Vector3 shortlistOrigin, shortlistForward;
        float refreshAt, shortlistAt;
        bool dense;

        readonly struct Receiver
        {
            internal readonly Renderer renderer;
            internal readonly float gloss;
            internal Receiver(Renderer renderer, float gloss)
            {
                this.renderer = renderer;
                this.gloss = gloss;
            }
        }

        readonly struct RankedReceiver
        {
            internal readonly Renderer renderer;
            internal readonly float priority;
            internal RankedReceiver(Renderer renderer, float priority)
            {
                this.renderer = renderer;
                this.priority = priority;
            }
        }
        bool attemptedLoad;

        sealed class Draw
        {
            internal Material material;
            internal TextureHandle source;
            internal RendererListHandle list;
            internal Vector4 settings, fog, sun;
            internal int pass;
        }

        internal H3VRScreenEffects()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            requiresIntermediateTexture = true;
        }

        internal bool Setup(Camera camera)
        {
            bool reflections = GraphicsOptions.Reflections != 0;
            if (!reflections && receivers.Count != 0)
            {
                ClearReceivers();
                candidates.Clear();
                markedScene = default;
            }
            if (camera.orthographic || (!H3VREyeRendering.IsPlayerStereoEye(camera) && camera.targetTexture) ||
                !reflections && (GraphicsOptions.FogQuality == 0 || !RenderSettings.fog))
                return false;

            if (GetMaterial() && reflections)
                UpdateReceivers(camera);
            ConfigureInput(ScriptableRenderPassInput.Depth |
                (reflections && receivers.Count != 0 ? ScriptableRenderPassInput.Color : ScriptableRenderPassInput.None));
            return material;
        }

        internal Material GetMaterial()
        {
            if (!material && !attemptedLoad)
            {
                attemptedLoad = true;
                var shader = Resources.Load<Shader>("NativeH3VRScreenEffects");
                if (!shader)
                {
                    string path = Path.Combine(Application.streamingAssetsPath, "NativeH3VR", BundleName);
                    if (File.Exists(path))
                    {
                        var bundle = AssetBundle.LoadFromFile(path);
                        if (bundle)
                        {
                            shader = System.Array.Find(bundle.LoadAllAssets<Shader>(), asset => asset.name == ShaderName);
                            bundle.Unload(false);
                        }
                    }
                }
                if (shader && shader.isSupported)
                    material = CoreUtils.CreateEngineMaterial(shader);
            }

            return material;
        }

        internal void Release()
        {
            ClearReceivers();
            CoreUtils.Destroy(material);
        }

        void ClearReceivers()
        {
            foreach (var receiver in receivers)
                if (receiver) receiver.renderingLayerMask &= ~ReceiverLayer;
            receivers.Clear();
            LastReceiverCount = 0;
        }

        void UpdateReceivers(Camera camera)
        {
            var scene = SceneManager.GetActiveScene();
            float now = Time.realtimeSinceStartup;
            bool refresh = scene != markedScene || now >= refreshAt;
            if (refresh)
            {
                ClearReceivers();
                candidates.Clear();
                markedScene = scene;
                refreshAt = now + 3f;

                // Eligibility is relatively static. Reuse the material list and
                // keep a slower refresh for spawned props outside the scene load.
                foreach (var renderer in Object.FindObjectsByType<MeshRenderer>())
                {
                    if (!renderer.enabled || renderer.forceRenderingOff || renderer.gameObject.scene != scene ||
                        (renderer.renderingLayerMask & ReceiverLayer) != 0)
                        continue;
                    renderer.GetSharedMaterials(surfaces);
                    if (surfaces.Count != 0 && TryGetGloss(surfaces, out float gloss))
                        candidates.Add(new Receiver(renderer, gloss));
                }
                surfaces.Clear();
                dense = candidates.Count > DenseReceiverThreshold;
            }

            if (!dense)
            {
                if (refresh)
                    foreach (var candidate in candidates)
                        Mark(candidate.renderer);
                LastReceiverCount = receivers.Count;
                return;
            }

            var position = camera.transform.position;
            var forward = camera.transform.forward;
            if (!refresh && now < shortlistAt &&
                (position - shortlistOrigin).sqrMagnitude < 1f && Vector3.Dot(forward, shortlistForward) > 0.985f)
                return;

            shortlistOrigin = position;
            shortlistForward = forward;
            shortlistAt = now + .25f;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);

            shortlist.Clear();
            foreach (var candidate in candidates)
            {
                if (!candidate.renderer || !candidate.renderer.enabled)
                    continue;
                Bounds bounds = candidate.renderer.bounds;
                float distance = bounds.SqrDistance(position);
                if (distance > DenseRangeSquared)
                    continue;

                Vector3 dimensions = bounds.size;
                float area = Mathf.Max(dimensions.x * dimensions.z,
                    Mathf.Max(dimensions.x * dimensions.y, dimensions.y * dimensions.z));
                // Use a padded volume for both eyes and slight head motion.
                // Give large contiguous receivers priority over small details.
                bounds.Expand(1.5f);
                if (!GeometryUtility.TestPlanesAABB(planes, bounds))
                    continue;
                float priority = (1f + Mathf.Min(area, 48f)) * candidate.gloss / (4f + distance);
                shortlist.Add(new RankedReceiver(candidate.renderer, priority));
            }

            shortlist.Sort(static (a, b) => b.priority.CompareTo(a.priority));
            selected.Clear();
            int count = Mathf.Min(DenseReceiverLimit, shortlist.Count);
            for (int i = 0; i < count; ++i)
                selected.Add(shortlist[i].renderer);
            for (int i = receivers.Count - 1; i >= 0; --i)
            {
                var receiver = receivers[i];
                if (receiver && selected.Contains(receiver))
                    continue;
                if (receiver)
                    receiver.renderingLayerMask &= ~ReceiverLayer;
                receivers.RemoveAt(i);
            }
            for (int i = 0; i < count; ++i)
                Mark(shortlist[i].renderer);
            LastReceiverCount = receivers.Count;
        }

        void Mark(Renderer renderer)
        {
            if (!renderer || (renderer.renderingLayerMask & ReceiverLayer) != 0)
                return;
            renderer.renderingLayerMask |= ReceiverLayer;
            receivers.Add(renderer);
        }

        static bool TryGetGloss(List<Material> materials, out float gloss)
        {
            gloss = 0f;
            foreach (var surface in materials)
            {
                if (!surface || !surface.shader || surface.renderQueue > (int)RenderQueue.GeometryLast ||
                    surface.shader.name is not ("NativeH3VR/Stock PBR" or "Alloy/Core" or "Alloy/Oriented/Core") ||
                    !surface.HasProperty("_GlossyReflections") || surface.GetFloat("_GlossyReflections") == 0f ||
                    surface.IsKeywordEnabled("_GLOSSYREFLECTIONS_OFF") || surface.GetFloat("_Mode") != 0f)
                    return false;

                float strength = Mathf.Max(surface.GetFloat("_Metal"), .35f * surface.GetFloat("_Specularity"));
                if (strength < .035f)
                    return false;
                gloss = Mathf.Max(gloss, strength / (.5f + Mathf.Max(0, surface.GetFloat("_Roughness"))));
            }
            return true;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid())
                return;
            var camera = frameData.Get<UniversalCameraData>();
            var lights = frameData.Get<UniversalLightData>();
            bool reflections = GraphicsOptions.Reflections != 0 && receivers.Count != 0 &&
                resources.cameraOpaqueTexture.IsValid();
            int fogQuality = GraphicsOptions.FogQuality;
            bool shafts = fogQuality != 0 && RenderSettings.fog && lights.mainLightIndex >= 0 &&
                resources.mainShadowsTexture.IsValid();
            Vector4 sun = default;
            if (shafts)
            {
                var light = lights.visibleLights[lights.mainLightIndex];
                Vector3 direction = -light.localToWorldMatrix.GetColumn(2);
                sun = new Vector4(light.finalColor.r, light.finalColor.g, light.finalColor.b, 0);
                shafts = light.lightType == LightType.Directional && light.light && light.light.shadows != LightShadows.None &&
                    direction.sqrMagnitude > 0.9f;
            }
            if (!reflections && !shafts)
                return;

            var color = resources.activeColorTexture;
            var depth = resources.cameraDepthTexture;
            var desc = graph.GetTextureDesc(color);
            var size = desc.CalculateFinalDimensions();
            desc.depthBufferBits = DepthBits.None;
            desc.msaaSamples = MSAASamples.None;
            desc.bindTextureMS = false;
            desc.clearBuffer = false;
            desc.filterMode = FilterMode.Bilinear;
            desc.sizeMode = TextureSizeMode.Explicit;
            desc.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
            var settings = new Vector4(reflections ? 1 : 0, shafts ? fogQuality : 0, dense ? 1 : 0, 0);
            var fog = new Vector4((int)RenderSettings.fogMode, RenderSettings.fogDensity,
                RenderSettings.fogStartDistance, RenderSettings.fogEndDistance);

            if (reflections)
            {
                var rendering = frameData.Get<UniversalRenderingData>();
                int divisor = dense ? 3 : 2;
                desc.width = Mathf.Max(1, (size.x + divisor - 1) / divisor);
                desc.height = Mathf.Max(1, (size.y + divisor - 1) / divisor);
                var maskDesc = desc;
                maskDesc.colorFormat = GraphicsFormat.R8G8_UNorm;
                maskDesc.filterMode = FilterMode.Point;
                maskDesc.clearBuffer = true;
                maskDesc.clearColor = Color.clear;
                maskDesc.name = "H3VR reflective surfaces";
                var mask = graph.CreateTexture(maskDesc);
                using (var builder = graph.AddRasterRenderPass<Draw>("H3VR | Reflection receivers", out var data))
                {
                    var drawing = RenderingUtils.CreateDrawingSettings(OpaqueTags, rendering, camera, lights, camera.defaultOpaqueSortFlags);
                    drawing.overrideShader = material.shader;
                    drawing.overrideShaderPassIndex = 0;
                    var filter = new FilteringSettings(RenderQueueRange.opaque, -1, ReceiverLayer);
                    data.list = graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filter));
                    builder.UseRendererList(data.list);
                    builder.UseTexture(depth);
                    builder.SetRenderAttachment(mask, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(mask, Mask);
                    builder.SetRenderFunc(static (Draw pass, RasterGraphContext context) => context.cmd.DrawRendererList(pass.list));
                }

                desc.name = "H3VR reflection trace";
                using var tracer = graph.AddRasterRenderPass<Draw>("H3VR | Depth-traced reflections", out var trace);
                tracer.UseTexture(depth);
                tracer.UseGlobalTexture(Mask);
                tracer.SetGlobalTextureAfterPass(RecordColorOutput(graph, tracer, trace, resources.cameraOpaqueTexture, desc, 1, settings, fog, sun), Ssr);
            }

            if (shafts)
            {
                int divisor = fogQuality == 2 ? 3 : 4;
                desc.width = Mathf.Max(1, (size.x + divisor - 1) / divisor);
                desc.height = Mathf.Max(1, (size.y + divisor - 1) / divisor);
                desc.name = "H3VR directional volume";
                using var tracer = graph.AddRasterRenderPass<Draw>("H3VR | Shadowed directional in-scattering", out var trace);
                tracer.UseTexture(resources.mainShadowsTexture);
                tracer.UseGlobalTexture(MainShadows);
                tracer.SetGlobalTextureAfterPass(RecordColorOutput(graph, tracer, trace, depth, desc, 2, settings, fog, sun), Shafts);
            }

            using var combine = graph.AddRasterRenderPass<Draw>("H3VR | Composite screen effects", out var composite);
            if (reflections)
            {
                combine.UseGlobalTexture(Mask);
                combine.UseGlobalTexture(Ssr);
            }
            if (shafts) combine.UseGlobalTexture(Shafts);
            combine.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
            combine.UseTexture(depth);
            SetDraw(combine, composite, depth, 3, settings, fog, sun);
        }

        TextureHandle RecordColorOutput(RenderGraph graph, IRasterRenderGraphBuilder builder, Draw draw,
            TextureHandle source, TextureDesc desc, int pass, Vector4 settings, Vector4 fog, Vector4 sun)
        {
            var target = graph.CreateTexture(desc);
            builder.UseTexture(source);
            builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
            SetDraw(builder, draw, source, pass, settings, fog, sun);
            return target;
        }

        void SetDraw(IRasterRenderGraphBuilder builder, Draw draw, TextureHandle source,
            int pass, Vector4 settings, Vector4 fog, Vector4 sun)
        {
            draw.material = material;
            draw.source = source;
            draw.pass = pass;
            draw.settings = settings;
            draw.fog = fog;
            draw.sun = sun;
            builder.SetRenderFunc(static (Draw data, RasterGraphContext context) =>
            {
                data.material.SetVector(Effect, data.settings);
                data.material.SetVector(Fog, data.fog);
                data.material.SetVector(Sun, data.sun);
                Blitter.BlitTexture(context.cmd, data.source, Identity, data.material, data.pass);
                if (data.pass == 1) ++ReflectionTraces;
                if (data.pass == 2) ++FogIntegrations;
            });
        }
    }
}
