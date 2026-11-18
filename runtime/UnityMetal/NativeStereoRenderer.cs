using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace NativeH3VR
{
    [DefaultExecutionOrder(-10000)]
    public sealed class NativeStereoRenderer : MonoBehaviour
    {
        static readonly ProfilerMarker WaitMarker = new ProfilerMarker("NativeH3VR.OpenXR.Wait");
        static readonly ProfilerMarker StereoMarker = new ProfilerMarker("NativeH3VR.OpenXR.RenderStereo");

        public static NativeStereoRenderer Instance { get; private set; }
        public Camera SourceCamera { get; private set; }
        public Transform TrackingOrigin { get; private set; }
        public MetalOpenXR.Frame LatestFrame { get; private set; }
        public ulong Sequence => LatestFrame.sequence;
        public ulong CompletedFrames => status.completed;
        public uint EyeWidth => status.width;
        public uint EyeHeight => status.height;
        public bool Focused => status.focused != 0;
        public bool Running => installed && status.active != 0;
        public bool SuspendSceneRendering { get; set; }
        public bool IsRenderingEye { get; private set; }
        public Matrix4x4 EyeProjection { get; private set; }

        struct DisplayFade
        {
            Color from, to;
            double began, duration;

            public Color Current => duration == 0 ? to : Color.Lerp(
                from, to, (float)((Time.realtimeSinceStartupAsDouble - began) / duration));

            public void Set(Color target, float seconds)
            {
                from = Current;
                to = target;
                began = Time.realtimeSinceStartupAsDouble;
                duration = Mathf.Max(0, seconds);
            }
        }

        readonly RenderTexture[] targets = new RenderTexture[2];
        readonly IntPtr[] pointers = new IntPtr[2];
        MetalOpenXR.Status status;
        IntPtr renderEvent;
        bool sourceWasEnabled;
        bool installed;
        bool frameOpen;
        bool sawFocus;
        StreamWriter profile;
        ProfilerRecorder mainThread, renderThread, memory;
        double waitMs;
        long previousUpdateTicks;
        DisplayFade foreground, background, grid;

        public RenderTexture EyeTexture(int eye) => targets[eye];
        public Color FadeColor(bool inBackground) => inBackground ? background.Current : foreground.Current;
        public float GridAlpha => grid.Current.a;

        public void FadeTo(Color color, float seconds, bool inBackground)
        {
            if (inBackground) background.Set(color, seconds);
            else foreground.Set(color, seconds);
        }

        public void FadeGrid(float seconds, bool fadeIn) =>
            grid.Set(new Color(0, 0, 0, fadeIn ? 1 : 0), seconds);

        public static NativeStereoRenderer Install()
        {
            if (Instance != null) return Instance;
            if (Application.platform != RuntimePlatform.OSXPlayer || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                throw new PlatformNotSupportedException("NativeH3VR requires the macOS Metal player.");
            if (UnityEngine.XR.XRSettings.enabled && UnityEngine.XR.XRSettings.isDeviceActive)
                throw new InvalidOperationException("A Unity XR provider already owns the session.");

            var host = new GameObject("NativeH3VR Metal stereo");
            DontDestroyOnLoad(host);
            var renderer = host.AddComponent<NativeStereoRenderer>();
            try
            {
                renderer.Open();
                Instance = renderer;
                return renderer;
            }
            catch
            {
                Destroy(host);
                throw;
            }
        }

        void Open()
        {
            string loader = Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR_LOADER");
            MetalOpenXR.Open(loader, EyeSize("NATIVEH3VR_EYE_WIDTH"), EyeSize("NATIVEH3VR_EYE_HEIGHT"));
            installed = true;
            MetalOpenXR.Check(MetalOpenXR.nhxr_status(out status));
            int first = MetalOpenXR.nhxr_begin(out var frame);
            MetalOpenXR.Check(first);
            if (first == 0)
            {
                LatestFrame = frame;
                MetalOpenXR.Check(MetalOpenXR.nhxr_cancel());
            }
            renderEvent = MetalOpenXR.nhxr_render_event();
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            string profilePath = Environment.GetEnvironmentVariable("NATIVEH3VR_UNITY_TRACE");
            if (!string.IsNullOrEmpty(profilePath))
            {
                profile = new StreamWriter(new FileStream(profilePath, FileMode.CreateNew, FileAccess.Write));
                mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
                renderThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 1);
                memory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory", 1);
                profile.WriteLine("unity_frame,loop_ms,xr_sequence,xr_wait_ms,eye_render_cpu_ms,stereo_completed,stereo_skipped," +
                                  "display_period_ns,xr_display_ns,completed_sequence,main_thread_ns,render_thread_ns,unity_metal_gpu_ns,end_frame_ns,system_used_bytes");
            }
            StartCoroutine(RenderAtEndOfFrame());
        }

        static uint EyeSize(string variable)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value)) return 0;
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint size) || size == 0)
                throw new ArgumentException("Invalid eye size in " + variable);
            return size;
        }

        public void BindCamera(Camera camera, Transform origin)
        {
            if (camera == null || origin == null || camera.transform == origin)
                throw new ArgumentException("Supply a camera and a separate tracking origin.");
            if (camera == SourceCamera && origin == TrackingOrigin) return;
            RestoreCamera();
            SourceCamera = camera;
            TrackingOrigin = origin;
            sourceWasEnabled = camera.enabled;
            camera.enabled = false;
            ApplyHead();
        }

        void RestoreCamera()
        {
            if (SourceCamera != null) SourceCamera.enabled = sourceWasEnabled;
            SourceCamera = null;
            TrackingOrigin = null;
        }

        void OnApplicationQuit() => Close();

        void OnDestroy()
        {
            Close();
            if (Instance == this) Instance = null;
        }

        void Close()
        {
            if (installed)
            {
                // The plugin retains textures until the submitted command buffer
                // completes. A queued Unity event will drain the same session.
                MetalOpenXR.nhxr_close();
                installed = frameOpen = false;
            }
            RestoreCamera();
            foreach (RenderTexture target in targets)
                if (target != null) Destroy(target);
            profile?.Dispose();
            profile = null;
            if (mainThread.Valid) mainThread.Dispose();
            if (renderThread.Valid) renderThread.Dispose();
            if (memory.Valid) memory.Dispose();
        }

        void ApplyHead()
        {
            if (SourceCamera == null || TrackingOrigin == null || Sequence == 0) return;
            var head = LatestFrame.head;
            SourceCamera.transform.SetPositionAndRotation(TrackingOrigin.TransformPoint(head.Position),
                                                          TrackingOrigin.rotation * head.Rotation);
        }

        void Update()
        {
            if (!installed) return;
            try
            {
                long start = Stopwatch.GetTimestamp();
                MetalOpenXR.Check(MetalOpenXR.nhxr_status(out status));
                int result;
                MetalOpenXR.Frame frame;
                using (WaitMarker.Auto())
                    result = MetalOpenXR.nhxr_begin(out frame);
                waitMs = Since(start);
                MetalOpenXR.Check(result);
                if (result == 2)
                {
                    Close();
                    Application.Quit(1);
                    return;
                }
                frameOpen = result == 0;
                if (frameOpen) LatestFrame = frame;
                MetalOpenXR.Check(MetalOpenXR.nhxr_status(out status));
                if (Focused != sawFocus)
                {
                    sawFocus = Focused;
                    Debug.Log("NATIVEH3VR_OPENXR_FOCUS " + Focused);
                }
                ApplyHead();
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        void Fail(Exception error)
        {
            Debug.LogException(error);
            Close();
            Application.Quit(1);
        }

        static double Since(long ticks) => (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
        static string Metric(ProfilerRecorder recorder) => recorder.Valid && recorder.Count > 0 && recorder.LastValue > 0
            ? recorder.LastValue.ToString(CultureInfo.InvariantCulture) : "";

        IEnumerator RenderAtEndOfFrame()
        {
            var end = new WaitForEndOfFrame();
            while (installed)
            {
                yield return end;
                if (!installed) yield break;
                long started = Stopwatch.GetTimestamp();
                double loopMs = previousUpdateTicks == 0 ? 0 :
                    (started - previousUpdateTicks) * 1000.0 / Stopwatch.Frequency;
                previousUpdateTicks = started;
                try
                {
                    bool rendered = frameOpen && SubmitFrame();
                    if (profile != null)
                    {
                        double eyeMs = rendered ? Since(started) : 0;
                        profile.WriteLine(FormattableString.Invariant(
                            $"{Time.frameCount},{loopMs:F4},{Sequence},{waitMs:F4},{eyeMs:F4},{status.completed},{status.skipped},{LatestFrame.displayPeriod},{LatestFrame.displayTime},{status.completedSequence},{Metric(mainThread)},{Metric(renderThread)},{status.unityGpuNs},{status.endFrameNs},{Metric(memory)}"));
                    }
                    frameOpen = false;
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }
        }

        bool SubmitFrame()
        {
            if (SourceCamera == null || TrackingOrigin == null || (SuspendSceneRendering && targets[0] == null))
            {
                MetalOpenXR.Check(MetalOpenXR.nhxr_cancel());
                return false;
            }

            using (StereoMarker.Auto())
            {
                var frame = LatestFrame;
                ApplyHead();
                for (int eye = 0; eye < 2; eye++)
                {
                    var target = EyeTarget(eye, (int)frame.width, (int)frame.height);
                    if (!SuspendSceneRendering) RenderEye(eye == 0 ? frame.left : frame.right, target);
                }
                var overlay = new MetalOpenXR.Overlay
                {
                    foreground = foreground.Current,
                    background = background.Current,
                    grid = GridAlpha,
                };
                MetalOpenXR.Check(MetalOpenXR.nhxr_queue(frame.sequence, pointers[0], pointers[1], in overlay));
                GL.IssuePluginEvent(renderEvent, 1);
            }
            return true;
        }

        RenderTexture EyeTarget(int eye, int width, int height)
        {
            if (targets[eye] == null)
            {
                var descriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R8G8B8A8_SRGB, 24);
                targets[eye] = new RenderTexture(descriptor) { name = eye == 0 ? "OpenXR left eye" : "OpenXR right eye" };
                pointers[eye] = IntPtr.Zero;
            }

            // GetNativeTexturePtr synchronizes Unity's render thread. The
            // resource is stable until the target is released or recreated.
            if (!targets[eye].IsCreated())
            {
                if (!targets[eye].Create()) throw new InvalidOperationException("Failed to create a Metal eye texture.");
                pointers[eye] = IntPtr.Zero;
            }
            if (pointers[eye] == IntPtr.Zero)
            {
                pointers[eye] = targets[eye].GetNativeTexturePtr();
                if (pointers[eye] == IntPtr.Zero) throw new InvalidOperationException("Unity returned no Metal eye texture.");
            }
            return targets[eye];
        }

        void RenderEye(MetalOpenXR.View view, RenderTexture target)
        {
            Camera camera = SourceCamera;
            Transform transform = camera.transform;
            Vector3 previousPosition = transform.position;
            Quaternion previousRotation = transform.rotation;
            Matrix4x4 previousProjection = camera.projectionMatrix;
            Matrix4x4 previousNonJittered = camera.nonJitteredProjectionMatrix;
            var previousTarget = camera.targetTexture;
            var previousEye = camera.stereoTargetEye;
            bool previousMSAA = camera.allowMSAA;
            try
            {
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.allowMSAA = false;
                camera.targetTexture = target;
                transform.SetPositionAndRotation(TrackingOrigin.TransformPoint(view.pose.Position),
                                                 TrackingOrigin.rotation * view.pose.Rotation);
                EyeProjection = view.Projection(camera.nearClipPlane, camera.farClipPlane);
                IsRenderingEye = true;
                camera.projectionMatrix = EyeProjection;
                camera.nonJitteredProjectionMatrix = EyeProjection;
                camera.Render();
            }
            finally
            {
                IsRenderingEye = false;
                transform.SetPositionAndRotation(previousPosition, previousRotation);
                camera.targetTexture = previousTarget;
                camera.stereoTargetEye = previousEye;
                camera.allowMSAA = previousMSAA;
                camera.projectionMatrix = previousProjection;
                camera.nonJitteredProjectionMatrix = previousNonJittered;
            }
        }
    }
}
