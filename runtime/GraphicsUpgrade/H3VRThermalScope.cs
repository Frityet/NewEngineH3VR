using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    public sealed class H3VRThermalScope : ScriptableRendererFeature
    {
        public Shader opaque, cutout, transparent;
        private ThermalPass pass;
        private static readonly ConditionalWeakTable<Camera, Shader> Replacements = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            Replacements.Clear();
            PIPScope.NativeH3VRSetThermalReplacement = SetReplacement;
        }

        private static void SetReplacement(Camera camera, Shader shader, string tag)
        {
            Replacements.Remove(camera);
            if (shader && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset &&
                shader.name == "Hidden/Thermal" && tag == "RenderType")
                Replacements.Add(camera, shader);
            else if (shader)
                camera.SetReplacementShader(shader, tag);
            else
                camera.ResetReplacementShader();
        }

        public override void Create() => pass = new ThermalPass(opaque, cutout, transparent);

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            if (Replacements.TryGetValue(data.cameraData.camera, out _))
                renderer.EnqueuePass(pass);
        }

        private sealed class ThermalPass : ScriptableRenderPass
        {
            private readonly Shader opaque, cutout, transparent;

            private sealed class Draw
            {
                internal RendererListHandle opaque, cutout, transparent;
            }

            internal ThermalPass(Shader opaque, Shader cutout, Shader transparent)
            {
                this.opaque = opaque;
                this.cutout = cutout;
                this.transparent = transparent;
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !resources.activeDepthTexture.IsValid()) return;
                var camera = frameData.Get<UniversalCameraData>();
                var rendering = frameData.Get<UniversalRenderingData>();
                var lights = frameData.Get<UniversalLightData>();
                var color = graph.GetTextureDesc(resources.activeColorTexture);
                color.name = "H3VR thermal camera color";
                color.clearBuffer = true;
                color.clearColor = CoreUtils.ConvertSRGBToActiveColorSpace(camera.camera.backgroundColor);
                var depth = graph.GetTextureDesc(resources.activeDepthTexture);
                depth.name = "H3VR thermal camera depth";
                depth.clearBuffer = true;
                var colorTarget = graph.CreateTexture(color);
                var depthTarget = graph.CreateTexture(depth);

                using var builder = graph.AddRasterRenderPass<Draw>("H3VR Thermal Scope", out var draw);
                draw.opaque = CreateList(graph, rendering, camera, lights, opaque, "Opaque", camera.defaultOpaqueSortFlags);
                draw.cutout = CreateList(graph, rendering, camera, lights, cutout, "TransparentCutout", camera.defaultOpaqueSortFlags);
                draw.transparent = CreateList(graph, rendering, camera, lights, transparent, "Transparent", SortingCriteria.CommonTransparent);
                builder.UseRendererList(draw.opaque);
                builder.UseRendererList(draw.cutout);
                builder.UseRendererList(draw.transparent);
                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (Draw data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(data.opaque);
                    context.cmd.DrawRendererList(data.cutout);
                    context.cmd.DrawRendererList(data.transparent);
                });
                resources.cameraColor = colorTarget;
                resources.cameraDepth = depthTarget;
            }

            private static RendererListHandle CreateList(RenderGraph graph, UniversalRenderingData rendering,
                UniversalCameraData camera, UniversalLightData lights, Shader replacement, string type,
                SortingCriteria sorting)
            {
                var tag = new ShaderTagId("NativeH3VRThermal" + type);
                var drawing = RenderingUtils.CreateDrawingSettings(tag, rendering, camera, lights, sorting);
                drawing.overrideShader = replacement;
                drawing.overrideShaderPassIndex = 0;
                var filter = new FilteringSettings(RenderQueueRange.all);
                return graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filter));
            }
        }
    }
}
