using UnityEngine;
using UnityEngine.Rendering;

namespace NativeH3VR
{
    public static class LegacyTargetRendering
    {
        public static void Attach(Camera camera, CameraEvent stage, CommandBuffer commands)
        {
            if (!GraphicsSettings.currentRenderPipeline) camera.AddCommandBuffer(stage, commands);
        }

        public static void Detach(Camera camera)
        {
            if (!GraphicsSettings.currentRenderPipeline) camera.RemoveAllCommandBuffers();
        }

        public static void Begin(CommandBuffer commands, Camera camera)
        {
            commands.Clear();
            if (!GraphicsSettings.currentRenderPipeline) return;

            var target = camera.targetTexture;
            commands.SetRenderTarget(target);
            commands.SetViewport(new Rect(0, 0, target.width, target.height));
            // These commands paint the paper's UV bitmap directly. Keep the
            // stock texture orientation while converting the GPU depth range.
            commands.SetViewProjectionMatrices(camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, false));
            commands.SetInvertCulling(false);
        }

        public static void Render(Camera camera, CommandBuffer commands)
        {
            if (!GraphicsSettings.currentRenderPipeline)
            {
                camera.Render();
                return;
            }

            // SRP does not execute Built-in camera events. Reuse the original
            // target background, stamps, tears, and render-texture copies.
            var previous = RenderTexture.active;
            commands.SetInvertCulling(GL.invertCulling);
            GL.PushMatrix();
            try { Graphics.ExecuteCommandBuffer(commands); }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previous;
            }
        }
    }
}
