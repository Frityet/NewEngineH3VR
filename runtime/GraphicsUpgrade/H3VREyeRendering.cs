using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NativeH3VR.GraphicsUpgrade
{
    public static class H3VREyeRendering
    {
        static readonly UniversalRenderPipeline.SingleCameraRequest Request = new UniversalRenderPipeline.SingleCameraRequest();
        static readonly TemporalEye[] Eyes = { new(), new() };
        static Camera currentEye;
        static Action<Camera> setHistoryCamera;
        static Camera historyCamera;

        sealed class TemporalEye
        {
            internal Camera camera;
            internal UniversalAdditionalCameraData data;
            internal Camera lastSource;
            internal Scene lastScene;
            internal Vector3 lastPosition;
            internal Quaternion lastRotation;
            internal Matrix4x4 lastProjection;
            internal double lastTime;
            internal bool valid;
        }
        static RenderTexture sceneColor;
        static int requestedSamples;
        static bool requestedHdr;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (GraphicsSettings.defaultRenderPipeline is not UniversalRenderPipelineAsset) return;
            XRBridge.RenderEye = Render;
            XRBridge.SelectRenderCamera = SelectCamera;
        }

        internal static void InvalidateTemporalHistory()
        {
            foreach (var eye in Eyes) eye.valid = false;
        }

        static Camera SelectCamera(Camera source, int index)
        {
            if (!GraphicsOptions.TemporalAA) return source;

            var eye = Eyes[index];
            if (!eye.camera)
            {
                var owner = new GameObject(index == 0 ? "NativeH3VR URP Left Eye" : "NativeH3VR URP Right Eye");
                owner.hideFlags = HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(owner);
                owner.SetActive(false);
                eye.camera = owner.AddComponent<Camera>();
                eye.camera.enabled = false;
                eye.data = owner.AddComponent<UniversalAdditionalCameraData>();
                eye.data.allowXRRendering = false;
                eye.data.renderPostProcessing = true;
                // The game's original grading runs in our composition pass.
                // Enable URP post for TAA without opting into an extra volume grade.
                eye.data.volumeLayerMask = 0;
                eye.data.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                eye.data.taaSettings.quality = TemporalAAQuality.High;
                eye.data.taaSettings.jitterScale = 0.65f;
                eye.data.taaSettings.baseBlendFactor = 0.2f;
                owner.SetActive(true);
                eye.valid = false;
            }

            var camera = eye.camera;
            camera.CopyFrom(source);
            camera.enabled = false;
            camera.allowDynamicResolution = false;
            camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            camera.worldToCameraMatrix = source.worldToCameraMatrix;
            camera.projectionMatrix = source.projectionMatrix;
            camera.nonJitteredProjectionMatrix = source.nonJitteredProjectionMatrix;
            // Let Unity cull this eye from its tracked view and asymmetric
            // projection, including directional casters that affect that view.
            camera.ResetCullingMatrix();

            var scene = SceneManager.GetActiveScene();
            var projection = camera.projectionMatrix;
            var position = camera.transform.position;
            var rotation = camera.transform.rotation;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!eye.valid || eye.lastSource != source || eye.lastScene != scene || now - eye.lastTime > 0.3 ||
                (eye.lastPosition - position).sqrMagnitude > 1f || Mathf.Abs(Quaternion.Dot(eye.lastRotation, rotation)) < 0.97f ||
                ProjectionChanged(eye.lastProjection, projection))
                eye.data.resetHistory = true;
            eye.valid = true;
            eye.lastSource = source;
            eye.lastScene = scene;
            eye.lastPosition = position;
            eye.lastRotation = rotation;
            eye.lastProjection = projection;
            eye.lastTime = now;
            return camera;
        }

        static bool ProjectionChanged(Matrix4x4 before, Matrix4x4 after)
        {
            for (int i = 0; i < 16; ++i)
                if (Mathf.Abs(before[i] - after[i]) > 0.025f) return true;
            return false;
        }

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
                bool temporalEye = camera == Eyes[0].camera || camera == Eyes[1].camera;
                if (temporalEye)
                {
                    GraphicsOptions.PrepareStereoInput(camera);
                    StockScopeCameraCallbacks.BeforeManualRender(XRBridge.SceneCameraForRender(camera), camera);
                }
                camera.allowHDR = GraphicsOptions.Hdr;
                camera.allowMSAA = GraphicsOptions.Samples > 1;
                Request.destination = SceneTarget(output, camera.allowHDR, GraphicsOptions.Samples);
                if (temporalEye)
                    SubmitTemporalRequest(camera);
                else
                    RenderPipeline.SubmitRenderRequest(camera, Request);
                if (Request.destination != output)
                {
                    GL.sRGBWrite = output.sRGB && QualitySettings.activeColorSpace == ColorSpace.Linear;
                    Graphics.Blit(Request.destination, output);
                }
            }
            finally
            {
                try { StockScopeCameraCallbacks.AfterManualRender(camera); }
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
        }

        static void SubmitTemporalRequest(Camera camera)
        {
            if (setHistoryCamera == null)
            {
                var method = typeof(UniversalRenderPipeline).Assembly
                    .GetType("UnityEngine.Rendering.Universal.TemporalAA")
                    ?.GetMethod("SetNativeH3VRHistoryCamera", BindingFlags.Static | BindingFlags.NonPublic,
                        null, new[] { typeof(Camera) }, null);
                if (method == null)
                    throw new InvalidOperationException("URP does not support persistent tracked-eye temporal history.");
                setHistoryCamera = (Action<Camera>)Delegate.CreateDelegate(typeof(Action<Camera>), method);
            }

            var previous = historyCamera;
            setHistoryCamera(camera);
            historyCamera = camera;
            try { RenderPipeline.SubmitRenderRequest(camera, Request); }
            finally
            {
                historyCamera = previous;
                setHistoryCamera(previous);
            }
        }

        static RenderTexture SceneTarget(RenderTexture output, bool hdr, int samples)
        {
            if (!hdr && samples == 1)
            {
                if (sceneColor) ReleaseSceneTarget();
                return output;
            }
            if (sceneColor && sceneColor.width == output.width && sceneColor.height == output.height &&
                requestedHdr == hdr && requestedSamples == samples && sceneColor.IsCreated())
                return sceneColor;

            ReleaseSceneTarget();
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

        static void ReleaseSceneTarget()
        {
            if (sceneColor) Object.Destroy(sceneColor);
            sceneColor = null;
        }

        internal static void Release()
        {
            if (XRBridge.RenderEye == Render) XRBridge.RenderEye = null;
            if (XRBridge.SelectRenderCamera == SelectCamera) XRBridge.SelectRenderCamera = null;
            ReleaseSceneTarget();
            foreach (var eye in Eyes)
            {
                if (eye.camera) Object.Destroy(eye.camera.gameObject);
                eye.camera = null;
                eye.data = null;
                eye.valid = false;
                eye.lastSource = null;
            }
        }
    }
}
