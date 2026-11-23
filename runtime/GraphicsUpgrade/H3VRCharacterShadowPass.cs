using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    internal sealed class H3VRCharacterShadowPass : ScriptableRenderPass
    {
        const int ShaderPass = 4;
        const int DrawLimit = 12;
        static readonly int MainShadows = Shader.PropertyToID("_MainLightShadowmapTexture");
        static readonly int Rect = Shader.PropertyToID("_H3VRCharacterRect");
        static readonly int Starts = Shader.PropertyToID("_H3VRCharacterStarts");
        static readonly int Ends = Shader.PropertyToID("_H3VRCharacterEnds");
        static readonly int Light = Shader.PropertyToID("_H3VRCharacterLight");
        static readonly int Settings = Shader.PropertyToID("_H3VRCharacterSettings");

        internal static int LastDrawCount { get; private set; }

        readonly MaterialPropertyBlock properties = new();
        Material material;
        H3VRCharacterShadowCasters.Actor[] actors;
        int actorCount;

        sealed class PassData
        {
            internal Material material;
            internal MaterialPropertyBlock properties;
            internal H3VRCharacterShadowCasters.Actor[] actors;
            internal Matrix4x4 view;
            internal Matrix4x4 projection;
            internal float near;
            internal int count;
        }

        internal H3VRCharacterShadowPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            requiresIntermediateTexture = true;
        }

        internal bool Setup(Camera camera, Material shaderMaterial)
        {
            LastDrawCount = 0;
            if (!shaderMaterial || shaderMaterial.passCount <= ShaderPass ||
                camera.orthographic || !H3VREyeRendering.IsPlayerView(camera))
                return false;
            material = shaderMaterial;
            actors = H3VRCharacterShadowCasters.Collect(camera, out actorCount);
            return actorCount > 0;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid())
                return;

            var camera = frameData.Get<UniversalCameraData>();
            using var builder = graph.AddRasterRenderPass<PassData>("H3VR | Character contact shadows", out var data);
            data.material = material;
            data.properties = properties;
            data.actors = actors;
            data.count = Mathf.Min(actorCount, DrawLimit);
            data.view = camera.GetViewMatrix();
            data.projection = GL.GetGPUProjectionMatrix(camera.GetProjectionMatrix(), true);
            data.near = camera.camera.nearClipPlane;
            builder.UseTexture(resources.cameraDepthTexture);
            if (resources.mainShadowsTexture.IsValid())
            {
                builder.UseTexture(resources.mainShadowsTexture);
                builder.UseGlobalTexture(MainShadows);
            }
            builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
            builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
            {
                for (int index = 0; index < pass.count; ++index)
                {
                    var actor = pass.actors[index];
                    Vector3 direction = Direction(actor.light);
                    float reach = actor.light.w > .5f ? .36f : 2.25f;
                    if (!ProjectInfluence(pass, actor.bounds, direction * reach, actor.light.w > .5f, out var rect))
                        continue;
                    pass.properties.SetVector(Rect, rect);
                    pass.properties.SetVectorArray(Starts, actor.starts);
                    pass.properties.SetVectorArray(Ends, actor.ends);
                    pass.properties.SetVector(Light, direction);
                    pass.properties.SetVector(Settings,
                        new Vector4(actor.capsuleCount, reach, actor.strength, actor.light.w));
                    context.cmd.DrawProcedural(Matrix4x4.identity, pass.material, ShaderPass,
                        MeshTopology.Triangles, 6, 1, pass.properties);
                    ++LastDrawCount;
                }
            });
        }

        static Vector3 Direction(Vector4 light)
        {
            // A minimum elevation keeps nearly horizontal luminaires from extending the
            // local indirect shadow onto adjacent surfaces far from the actor.
            var direction = new Vector3(light.x, Mathf.Max(.58f, light.y), light.z);
            return direction.normalized;
        }

        static bool ProjectInfluence(PassData pass, Bounds body, Vector3 reach, bool realtimeSun, out Vector4 rect)
        {
            body.Encapsulate(body.min - reach);
            body.Encapsulate(body.max - reach);
            if (!realtimeSun)
                body.Encapsulate(body.min - Vector3.up * 1.35f);
            body.Expand(realtimeSun ? .42f : 1.3f);
            var min = body.min;
            var max = body.max;
            var lower = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var upper = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var near = Mathf.Max(.001f, pass.near);
            bool projected = false;
            for (int i = 0; i < 8; ++i)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                var view = pass.view.MultiplyPoint3x4(corner);
                if (view.z <= -near)
                {
                    ProjectCorner(pass.projection, view, ref lower, ref upper);
                    projected = true;
                    continue;
                }

                // Clip the three adjacent edges to the near plane so a behind-eye
                // corner never expands this draw into an unrelated screen quadrant.
                for (int axis = 0; axis < 3; ++axis)
                {
                    var opposite = corner;
                    opposite[axis] = (i & (1 << axis)) == 0 ? max[axis] : min[axis];
                    var end = pass.view.MultiplyPoint3x4(opposite);
                    if (end.z > -near)
                        continue;
                    var clipped = Vector3.LerpUnclamped(view, end, (-near - view.z) / (end.z - view.z));
                    ProjectCorner(pass.projection, clipped, ref lower, ref upper);
                    projected = true;
                }
            }
            lower = Vector2.Max(Vector2.one * -1f, lower - Vector2.one * .008f);
            upper = Vector2.Min(Vector2.one, upper + Vector2.one * .008f);
            rect = new Vector4(lower.x, lower.y, upper.x, upper.y);
            return projected && lower.x < upper.x && lower.y < upper.y;
        }

        static void ProjectCorner(Matrix4x4 projection, Vector3 point, ref Vector2 lower, ref Vector2 upper)
        {
            var clip = projection * new Vector4(point.x, point.y, point.z, 1f);
            var ndc = new Vector2(clip.x, clip.y) / clip.w;
            lower = Vector2.Min(lower, ndc);
            upper = Vector2.Max(upper, ndc);
        }
    }
}
