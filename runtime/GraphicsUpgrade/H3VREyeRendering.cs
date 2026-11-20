using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    public static class H3VREyeRendering
    {
        static readonly UniversalRenderPipeline.SingleCameraRequest Request = new UniversalRenderPipeline.SingleCameraRequest();
        static Camera currentEye;
        static RenderTexture sceneColor;
        static int requestedSamples;
        static bool requestedHdr;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install() => XRBridge.RenderEye = Render;

        internal static bool IsPlayerStereoEye(Camera camera) =>
            camera && camera.cameraType == CameraType.Game &&
            (camera == currentEye || (!camera.orthographic && camera.stereoEnabled));

        internal static bool IsPlayerView(Camera camera) =>
            camera && camera.cameraType == CameraType.Game &&
            (camera == currentEye || (!camera.orthographic && (camera.stereoEnabled || !camera.targetTexture)));

        // URP 17.7 derives the intermediate format and MSAA from camera.targetTexture.
        // The compositor accepts a resolved, display-referred sRGB texture. Keep that texture
        // and its native pointer stable while the user changes HDR, scale or MSAA.
        public static void Render(Camera camera, RenderTexture output)
        {
            bool previousHdr = camera.allowHDR;
            bool previousMsaa = camera.allowMSAA;
            var previousActive = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            try
            {
                currentEye = camera;
                camera.allowHDR = GraphicsOptions.Hdr;
                camera.allowMSAA = GraphicsOptions.Samples > 1;
                Request.destination = SceneTarget(output, camera.allowHDR, GraphicsOptions.Samples);
                RenderPipeline.SubmitRenderRequest(camera, Request);
                if (Request.destination != output)
                {
                    GL.sRGBWrite = output.sRGB && QualitySettings.activeColorSpace == ColorSpace.Linear;
                    Graphics.Blit(Request.destination, output);
                }
            }
            finally
            {
                Request.destination = null;
                currentEye = null;
                camera.allowHDR = previousHdr;
                camera.allowMSAA = previousMsaa;
                RenderTexture.active = previousActive;
                GL.sRGBWrite = previousSrgbWrite;
            }
        }

        static RenderTexture SceneTarget(RenderTexture output, bool hdr, int samples)
        {
            if (!hdr && samples == 1)
            {
                if (sceneColor) Release();
                return output;
            }
            if (sceneColor && sceneColor.width == output.width && sceneColor.height == output.height &&
                requestedHdr == hdr && requestedSamples == samples && sceneColor.IsCreated())
                return sceneColor;

            Release();
            var descriptor = output.descriptor;
            if (hdr)
            {
                var packed = GraphicsFormat.B10G11R11_UFloatPack32;
                descriptor.graphicsFormat = SystemInfo.IsFormatSupported(packed, GraphicsFormatUsage.Blend)
                    ? packed : SystemInfo.GetGraphicsFormat(DefaultFormat.HDR);
            }
            descriptor.msaaSamples = samples;
            descriptor.msaaSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
            descriptor.bindMS = false;
            sceneColor = new RenderTexture(descriptor) { name = "H3VR eye scene color", filterMode = FilterMode.Bilinear };
            if (!sceneColor.Create()) throw new InvalidOperationException("Could not allocate the URP eye render texture.");
            requestedHdr = hdr;
            requestedSamples = samples;
            return sceneColor;
        }

        internal static void Release()
        {
            if (sceneColor) UnityEngine.Object.Destroy(sceneColor);
            sceneColor = null;
        }
    }
}
