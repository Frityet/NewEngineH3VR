using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    public sealed class H3VRLegacyLighting : ScriptableRendererFeature
    {
        private LegacyLightPass pass;

        public override void Create()
        {
            pass = new LegacyLightPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(pass);
        }

        private sealed class LegacyLightPass : ScriptableRenderPass
        {
            private static readonly int Direction = Shader.PropertyToID("_WorldSpaceLightPos0");
            private static readonly int LightColor = Shader.PropertyToID("_LightColor0");

            private sealed class PassData
            {
                public Vector4 direction;
                public Color color;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var lights = frameData.Get<UniversalLightData>();
                using var builder = graph.AddRasterRenderPass<PassData>("Stock forward light uniforms", out var data);
                // A valid dummy direction keeps the recovered BRDF calculations finite on
                // probe-only cameras. Black leaves lightmaps and ambient probes in control.
                data.direction = new Vector4(0.26726124f, 0.80178373f, 0.53452248f, 0);
                data.color = Color.black;
                if (lights.mainLightIndex >= 0)
                {
                    var main = lights.visibleLights[lights.mainLightIndex];
                    if (main.lightType == LightType.Directional)
                    {
                        data.direction = -main.localToWorldMatrix.GetColumn(2);
                        data.direction.w = 0;
                        data.color = main.finalColor;
                    }
                }
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData values, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalVector(Direction, values.direction);
                    context.cmd.SetGlobalVector(LightColor, values.color);
                });
            }
        }
    }
}
