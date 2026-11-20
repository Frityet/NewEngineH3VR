using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    public sealed class H3VRStockEffects : ScriptableRendererFeature
    {
        public Shader trailShader;
        public Shader screenShakeShader;

        private Material trailMaterial;
        private Material shakeMaterial;
        private TrailPass trails;
        private FeedbackPass feedback;

        public override void Create()
        {
            ReleaseMaterials();
            trailMaterial = trailShader ? CoreUtils.CreateEngineMaterial(trailShader) : null;
            shakeMaterial = screenShakeShader ? CoreUtils.CreateEngineMaterial(screenShakeShader) : null;
            trails = new TrailPass(trailMaterial);
            feedback = new FeedbackPass(shakeMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (camera.cameraType != CameraType.Game)
                return;

            if (trailMaterial && trails.HasTrails())
                renderer.EnqueuePass(trails);

            if (shakeMaterial && camera.TryGetComponent<ScreenShakeVR>(out var effect) &&
                effect.isActiveAndEnabled && FeedbackPass.IsShaking(effect))
                renderer.EnqueuePass(feedback);
        }

        protected override void Dispose(bool disposing) => ReleaseMaterials();

        private void ReleaseMaterials()
        {
            CoreUtils.Destroy(trailMaterial);
            CoreUtils.Destroy(shakeMaterial);
        }

        private sealed class TrailPass : ScriptableRenderPass
        {
            private static readonly FieldInfo RegisteredTrails = typeof(VRTrailManager).GetField(
                "m_trails", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly int Positions = Shader.PropertyToID("_H3VRTrailPositions");
            private static readonly int ColorId = Shader.PropertyToID("_H3VRTrailColor");
            private readonly Material material;

            private sealed class Draws
            {
                public Material material;
                public List<Trail> trails;
            }

            private readonly struct Trail
            {
                public readonly ComputeBuffer buffer;
                public readonly Color color;
                public readonly int count;

                public Trail(VRTrail trail)
                {
                    buffer = trail.Buffer;
                    color = trail.Color;
                    count = trail.NumPositions - 1;
                }
            }

            public TrailPass(Material material)
            {
                this.material = material;
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            }

            private static List<VRTrail> GetTrails()
            {
                var manager = VRTrailManager.Instance;
                return manager && manager.isActiveAndEnabled ? RegisteredTrails?.GetValue(manager) as List<VRTrail> : null;
            }

            private static bool IsDrawable(VRTrail trail) =>
                trail && trail.NumPositions > 2 && trail.Buffer != null && trail.Buffer.IsValid();

            public bool HasTrails()
            {
                var registered = GetTrails();
                if (registered == null) return false;
                foreach (var trail in registered)
                    if (IsDrawable(trail)) return true;
                return false;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var registered = GetTrails();
                if (registered == null || !resources.activeColorTexture.IsValid() ||
                    !resources.activeDepthTexture.IsValid()) return;

                var draws = new List<Trail>();
                foreach (var trail in registered)
                {
                    if (!IsDrawable(trail)) continue;
                    trail.SetData();
                    draws.Add(new Trail(trail));
                }

                if (draws.Count == 0) return;
                using var builder = graph.AddRasterRenderPass<Draws>("H3VR | Stock trails", out var data);
                data.material = material;
                data.trails = draws;
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.SetRenderFunc(static (Draws values, RasterGraphContext context) =>
                {
                    var properties = new MaterialPropertyBlock();
                    foreach (var trail in values.trails)
                    {
                        properties.SetBuffer(Positions, trail.buffer);
                        properties.SetColor(ColorId, trail.color);
                        context.cmd.DrawProcedural(Matrix4x4.identity, values.material, 0,
                            MeshTopology.LineStrip, trail.count, 1, properties);
                    }
                });
            }
        }

        private sealed class FeedbackPass : ScriptableRenderPass
        {
            private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
            private static readonly FieldInfo Shake = typeof(ScreenShakeVR).GetField("shakeVal", Fields);
            private static readonly FieldInfo Vignette = typeof(ScreenShakeVR).GetField("vignetteVal", Fields);
            private static readonly FieldInfo Radius = typeof(ScreenShakeVR).GetField("vignetteRadius", Fields);
            private static readonly FieldInfo Tint = typeof(ScreenShakeVR).GetField("vignetteColor", Fields);
            private static readonly int Profile = Shader.PropertyToID("_H3VRFeedbackProfile");
            private static readonly int TintColor = Shader.PropertyToID("_H3VRFeedbackTint");
            private readonly Material material;

            public FeedbackPass(Material material)
            {
                this.material = material;
                // The stock image effect operates on the completed camera image.
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public static bool IsShaking(ScreenShakeVR effect) =>
                Shake?.GetValue(effect) is float strength && !Mathf.Approximately(strength, 0f);

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>().camera;
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !camera.TryGetComponent<ScreenShakeVR>(out var effect) ||
                    !effect.isActiveAndEnabled || Shake?.GetValue(effect) is not float shake ||
                    Mathf.Approximately(shake, 0f)) return;

                var source = resources.activeColorTexture;
                var desc = graph.GetTextureDesc(source);
                desc.depthBufferBits = DepthBits.None;
                desc.msaaSamples = MSAASamples.None;
                desc.bindTextureMS = false;
                desc.clearBuffer = false;
                desc.name = "H3VR stock camera feedback color";
                var target = graph.CreateTexture(desc);
                var properties = new MaterialPropertyBlock();
                properties.SetVector(Profile, new Vector4(shake,
                    Vignette?.GetValue(effect) is float vignette ? vignette : 0f,
                    Radius?.GetValue(effect) is float radius ? radius : 0f, 0f));
                properties.SetColor(TintColor, Tint?.GetValue(effect) is Color tint ? tint : Color.clear);
                var blit = new RenderGraphUtils.BlitMaterialParameters(source, target, material, 0, properties,
                    destinationSlice: 0, destinationMip: 0, geometry: RenderGraphUtils.FullScreenGeometryType.ProceduralTriangle);
                graph.AddBlitPass(blit, "H3VR | Stock camera feedback");
                resources.cameraColor = target;
            }
        }
    }
}
