using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    public sealed class H3VRRendererFeature : ScriptableRendererFeature
    {
        public Shader compositionShader;
        Material material;
        CompositionPass composition;

        public override void Create()
        {
            CoreUtils.Destroy(material);
            material = compositionShader ? CoreUtils.CreateEngineMaterial(compositionShader) : null;
            composition = new CompositionPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!material || renderingData.cameraData.renderType != CameraRenderType.Base ||
                !H3VREyeRendering.IsPlayerView(renderingData.cameraData.camera)) return;
            if (GraphicsOptions.Glow == 0 && GraphicsOptions.Sharpness == 0 &&
                GraphicsOptions.Tone == 0 && GraphicsOptions.ExposureStops == 0) return;
            composition.Material = material;
            renderer.EnqueuePass(composition);
        }

        protected override void Dispose(bool disposing) => CoreUtils.Destroy(material);

        sealed class CompositionPass : ScriptableRenderPass
        {
            static readonly int GlowTexture = Shader.PropertyToID("_H3VRGlowTexture");
            static readonly int Look = Shader.PropertyToID("_H3VRLook");
            static readonly int Exposure = Shader.PropertyToID("_H3VRExposure");
            static readonly Vector4 Identity = new Vector4(1, 1, 0, 0);
            internal Material Material;

            sealed class Draw
            {
                internal TextureHandle source;
                internal Material material;
                internal int pass;
            }

            internal CompositionPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return;
                var source = resources.activeColorTexture;
                var desc = graph.GetTextureDesc(source);
                // The color attachment can be multisampled, but filters consume its resolved color.
                desc.depthBufferBits = DepthBits.None;
                desc.msaaSamples = MSAASamples.None;
                desc.bindTextureMS = false;
                desc.clearBuffer = false;
                desc.filterMode = FilterMode.Bilinear;
                desc.name = "H3VR composed color";
                var result = graph.CreateTexture(desc);
                bool bloom = GraphicsOptions.Glow > 0;
                Material.SetVector(Look, new Vector4(GraphicsOptions.Glow, GraphicsOptions.Sharpness,
                    GraphicsOptions.Tone, GraphicsOptions.Hdr ? 1.05f : 0.82f));
                Material.SetFloat(Exposure, Mathf.Pow(2f, GraphicsOptions.ExposureStops));

                if (bloom)
                {
                    // One sixteenth as many pixels. Preserve texture-array slices for single-pass XR.
                    var camera = frameData.Get<UniversalCameraData>();
                    desc.sizeMode = TextureSizeMode.Explicit;
                    desc.width = Mathf.Max(1, (camera.cameraTargetDescriptor.width + 3) / 4);
                    desc.height = Mathf.Max(1, (camera.cameraTargetDescriptor.height + 3) / 4);
                    desc.name = "H3VR emissive highlights";
                    var highlights = graph.CreateTexture(desc);
                    using var extract = graph.AddRasterRenderPass<Draw>("H3VR | Extract highlights", out var data);
                    SetDraw(extract, data, source, highlights, 0);
                    extract.SetGlobalTextureAfterPass(highlights, GlowTexture);
                }

                using (var combine = graph.AddRasterRenderPass<Draw>("H3VR | Compose player view", out var data))
                {
                    if (bloom) combine.UseGlobalTexture(GlowTexture);
                    SetDraw(combine, data, source, result, bloom ? 2 : 1);
                }
                resources.cameraColor = result;
            }

            void SetDraw(IRasterRenderGraphBuilder builder, Draw draw, TextureHandle source, TextureHandle target, int pass)
            {
                draw.source = source;
                draw.material = Material;
                draw.pass = pass;
                builder.UseTexture(source);
                builder.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                builder.SetRenderFunc(static (Draw data, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, data.source, Identity, data.material, data.pass));
            }
        }
    }
}
