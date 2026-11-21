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
            // Stock vertex-lit particles have eight slots. Sulfur uses the first four.
            private const int VertexLightCount = 8;
            private static readonly int Direction = Shader.PropertyToID("_WorldSpaceLightPos0");
            private static readonly int LightColor = Shader.PropertyToID("_LightColor0");
            private static readonly int StockMainAlpha = Shader.PropertyToID("_H3VRStockMainLightAlpha");
            private static readonly int StockAdditionalLights = Shader.PropertyToID("_H3VRStockAdditionalLights");
            private static readonly int VertexColor = Shader.PropertyToID("_NativeH3VRVertexLightColor");
            private static readonly int VertexPosition = Shader.PropertyToID("_NativeH3VRVertexLightPosition");
            private static readonly int VertexAtten = Shader.PropertyToID("_NativeH3VRVertexLightAtten");
            private static readonly int VertexSpot = Shader.PropertyToID("_NativeH3VRVertexSpotDirection");

            private sealed class PassData
            {
                public Vector4 direction;
                public Color color;
                public float mainAlpha;
                public readonly Vector4[] stockAdditional = new Vector4[UniversalRenderPipeline.maxVisibleAdditionalLights];
                public readonly Vector4[] vertexColors = new Vector4[VertexLightCount];
                public readonly Vector4[] vertexPositions = new Vector4[VertexLightCount];
                public readonly Vector4[] vertexAttenuation = new Vector4[VertexLightCount];
                public readonly Vector4[] vertexSpots = new Vector4[VertexLightCount];
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var lights = frameData.Get<UniversalLightData>();
                using var builder = graph.AddRasterRenderPass<PassData>("Stock forward light uniforms", out var data);
                // A valid dummy direction keeps the recovered BRDF calculations finite on
                // probe-only cameras. Black leaves lightmaps and ambient probes in control.
                data.direction = new Vector4(0.26726124f, 0.80178373f, 0.53452248f, 0);
                data.color = Color.black;
                data.mainAlpha = 0f;
                System.Array.Clear(data.stockAdditional, 0, data.stockAdditional.Length);
                System.Array.Clear(data.vertexColors, 0, VertexLightCount);
                System.Array.Clear(data.vertexPositions, 0, VertexLightCount);
                System.Array.Clear(data.vertexAttenuation, 0, VertexLightCount);
                System.Array.Clear(data.vertexSpots, 0, VertexLightCount);
                int vertexCount = 0;
                if (lights.mainLightIndex >= 0)
                {
                    var main = lights.visibleLights[lights.mainLightIndex];
                    if (main.lightType == LightType.Directional)
                    {
                        data.direction = -main.localToWorldMatrix.GetColumn(2);
                        data.direction.w = 0;
                        data.color = main.finalColor;
                        data.mainAlpha = StockLightAlpha(main);
                        SetVertexLight(data, vertexCount++, main);
                    }
                }
                int budget = Mathf.Min(VertexLightCount, vertexCount + lights.maxPerObjectAdditionalLightsCount);
                for (int index = 0; index < lights.visibleLights.Length && vertexCount < budget; index++)
                {
                    if (index == lights.mainLightIndex) continue;
                    var light = lights.visibleLights[index];
                    if (light.lightType is LightType.Directional or LightType.Point or LightType.Spot)
                        SetVertexLight(data, vertexCount++, light);
                }
                // Match the URP shader light buffer's visible index order, excluding its main light.
                int additionalIndex = 0;
                for (int index = 0; index < lights.visibleLights.Length && additionalIndex < data.stockAdditional.Length; index++)
                {
                    if (index == lights.mainLightIndex) continue;
                    var light = lights.visibleLights[index];
                    data.stockAdditional[additionalIndex] = new Vector4(StockLightAlpha(light),
                        (float)light.lightType, light.range, 1f);
                    additionalIndex++;
                }
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData values, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalVector(Direction, values.direction);
                    context.cmd.SetGlobalVector(LightColor, values.color);
                    context.cmd.SetGlobalFloat(StockMainAlpha, values.mainAlpha);
                    context.cmd.SetGlobalVectorArray(StockAdditionalLights, values.stockAdditional);
                    context.cmd.SetGlobalVectorArray(VertexColor, values.vertexColors);
                    context.cmd.SetGlobalVectorArray(VertexPosition, values.vertexPositions);
                    context.cmd.SetGlobalVectorArray(VertexAtten, values.vertexAttenuation);
                    context.cmd.SetGlobalVectorArray(VertexSpot, values.vertexSpots);
                });
            }

            private static float StockLightAlpha(VisibleLight visible)
            {
                var light = visible.light;
                return light ? light.color.a * light.intensity : visible.finalColor.a;
            }

            private static void SetVertexLight(PassData data, int index, VisibleLight light)
            {
                data.vertexColors[index] = light.finalColor;
                Vector4 direction = -light.localToWorldMatrix.GetColumn(2);
                direction.w = 0;
                if (light.lightType == LightType.Directional)
                {
                    data.vertexPositions[index] = direction;
                    return;
                }

                Vector4 position = light.localToWorldMatrix.GetColumn(3);
                position.w = 1;
                float inverseRangeSquared = 1f / Mathf.Max(light.range * light.range, 0.0001f);
                float outerCone = light.lightType == LightType.Spot
                    ? Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad) : -1f;
                float angularScale = 1f / Mathf.Max(1f - outerCone, 0.001f);
                data.vertexPositions[index] = position;
                data.vertexSpots[index] = direction;
                data.vertexAttenuation[index] = new Vector4(outerCone, angularScale, 25f * inverseRangeSquared, 0);
            }
        }
    }
}
