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
            static readonly int BloomKernel = Shader.PropertyToID("_H3VRBloomKernel");
            static readonly int Look = Shader.PropertyToID("_H3VRLook");
            static readonly int Exposure = Shader.PropertyToID("_H3VRExposure");
            static readonly Vector4 Identity = new Vector4(1, 1, 0, 0);
            internal Material Material;

            sealed class Draw
            {
                internal TextureHandle source;
                internal Material material;
                internal int pass;
                internal Vector4 look;
                internal Vector4 kernel;
                internal float exposure;
                internal StockColorGrading.Binding grading;
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
                var camera = frameData.Get<UniversalCameraData>();
                StockColorGrading.Binding grading = default;
                int tone = GraphicsOptions.Tone;
                if (GraphicsOptions.UseStockGrading && !StockColorGrading.TryGetBinding(camera.camera, out grading))
                    tone = 0;
                var look = new Vector4(GraphicsOptions.Glow, GraphicsOptions.Sharpness, tone,
                    GraphicsOptions.Hdr ? 1.05f : 0.82f);
                float exposure = Mathf.Pow(2f, GraphicsOptions.ExposureStops);
                if (look.x == 0 && look.y == 0 && tone == 0 && exposure == 1) return;
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
                bool bloom = look.x > 0;

                if (bloom)
                {
                    // One sixteenth as many pixels. Preserve texture-array slices for single-pass XR.
                    var size = desc.CalculateFinalDimensions();
                    desc.sizeMode = TextureSizeMode.Explicit;
                    desc.width = Mathf.Max(1, (size.x + 3) / 4);
                    desc.height = Mathf.Max(1, (size.y + 3) / 4);
                    desc.name = "H3VR emissive highlights";
                    var highlights = graph.CreateTexture(desc);
                    using (var extract = graph.AddRasterRenderPass<Draw>("H3VR | Extract highlights", out var data))
                    {
                        SetDraw(extract, data, source, highlights, 0);
                        data.look = look;
                    }
                    desc.name = "H3VR filtered highlights";
                    var filtered = graph.CreateTexture(desc);
                    using (var filter = graph.AddRasterRenderPass<Draw>("H3VR | Filter highlights", out var data))
                    {
                        SetDraw(filter, data, highlights, filtered, 3);
                        data.kernel = GraphicsOptions.BloomKernel;
                        filter.SetGlobalTextureAfterPass(filtered, GlowTexture);
                    }
                }

                using (var combine = graph.AddRasterRenderPass<Draw>("H3VR | Compose player view", out var data))
                {
                    if (bloom) combine.UseGlobalTexture(GlowTexture);
                    SetDraw(combine, data, source, result, bloom ? 2 : 1);
                    data.look = look;
                    data.exposure = exposure;
                    data.grading = grading;
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
                {
                    if (data.pass == 3) data.material.SetVector(BloomKernel, data.kernel);
                    else data.material.SetVector(Look, data.look);
                    if (data.pass == 1 || data.pass == 2)
                    {
                        data.material.SetFloat(Exposure, data.exposure);
                        if (data.look.z > 2.5f) data.grading.Apply(data.material);
                    }
                    Blitter.BlitTexture(context.cmd, data.source, Identity, data.material, data.pass);
                });
            }
        }
    }
}
