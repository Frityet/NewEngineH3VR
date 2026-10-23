using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace NativeH3VR {
// Preserves the calibrated OpenXR eye projection only while the game's
// original source camera is rendering a native eye. Ordinary camera
// renders keep Unity's original ResetProjectionMatrix behavior.
public static class NativeEyeProjection {
    static Camera eyeCamera;
    static Matrix4x4 eyeProjection;
    public static bool IsRenderingEye {
        get { return eyeCamera != null; }
    }
    public static bool IsEyeCamera(Camera camera) { return camera != null && eyeCamera == camera; }
    public static void Begin(Camera camera, Matrix4x4 projection) {
        if (camera == null)
            throw new ArgumentNullException("camera");
        if (eyeCamera != null)
            throw new InvalidOperationException("Nested native eye render");
        eyeCamera = camera;
        eyeProjection = projection;
    }
    public static void End() { eyeCamera = null; }
    public static void ResetProjection(Camera camera) {
        if (IsEyeCamera(camera))
            camera.projectionMatrix = eyeProjection;
        else
            camera.ResetProjectionMatrix();
    }
}

public static class XRBridge {
    const string Library = "NativeH3VRXR";
    [StructLayout(LayoutKind.Sequential)]
    public struct Pose {
        public Vector3 position;
        public Quaternion rotation;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct View {
        public Pose pose;
        public float fovLeft, fovRight, fovUp, fovDown;
        public Matrix4x4 Projection(float near, float far) {
            return Matrix4x4.Frustum(Mathf.Tan(fovLeft) * near, Mathf.Tan(fovRight) * near,
                                     Mathf.Tan(fovDown) * near, Mathf.Tan(fovUp) * near, near, far);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Frame {
        public uint structSize, width, height, shouldRender;
        public long displayTimeNs;
        public ulong sequence, completedFrames;
        public View left, right;
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint nh3vr_xr_frame_size();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_start(string loader, string runtimeJson,
                                            uint syntheticHeadless, uint width, uint height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_begin(out Frame frame, uint size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_set_textures(IntPtr left, IntPtr right);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nh3vr_xr_render_event();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_status(out ulong completedFrames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint nh3vr_xr_focused();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_stop();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_cancel();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_haptic(uint role, float amplitude, float frequency,
                                             float duration);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern float nh3vr_xr_display_frequency();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_stage_bounds(out float width, out float depth);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_head_pose(out Pose pose);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nh3vr_xr_eye_views(out View left, out View right);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr nh3vr_xr_error();
    public static string Error {
        get { return Marshal.PtrToStringAnsi(nh3vr_xr_error()); }
    }
    public static void Check(int result) {
        if (result < 0)
            throw new InvalidOperationException("Native OpenXR: " + Error);
    }
}

// Optional benchmark evidence for the graphics integration. No game CPU
// optimizations live here; those remain in the portable OptiH3VR mod.
public static class NativeEyeTextureDiagnostics {
    public static int PointerQueries { get; internal set; }
    public static bool CacheEnabled { get; internal set; }
}

// Explicitly attach/configure this component after selecting the game camera
// and tracking-space root. It does not auto-start or disable SteamVR.
public sealed class NativeStereoRenderer : MonoBehaviour {
    public string loaderPath;
    public string runtimeManifest;
    public bool syntheticHeadless = true;
    public bool useExistingSession;
    public Camera sourceCamera;
    public Transform trackingOrigin;
    public uint eyeWidth = 1440, eyeHeight = 1600;
    public bool mirrorToWindow = true;
    public bool suspendSceneRendering;
    public ulong completedFrames { get; private set; }
    public XRBridge.Frame latestFrame { get; private set; }
    RenderTexture[] textures;
    bool initialized, sourceWasEnabled, stopping;

    public void BindCamera(Camera camera, Transform origin) {
        if (camera == null || origin == null)
            throw new ArgumentNullException("Native stereo requires camera and tracking origin");
        if (sourceCamera == camera && trackingOrigin == origin)
            return;
        if (initialized && sourceCamera != null)
            sourceCamera.enabled = sourceWasEnabled;
        sourceCamera = camera;
        trackingOrigin = origin;
        if (initialized) {
            sourceWasEnabled = sourceCamera.enabled;
            sourceCamera.enabled = false;
        }
        UpdateGameplayHeadPose();
    }

    // Legacy SteamVR hands the camera transform to Unity XR and disables
    // its old tracked-head component. Our renderer owns tracking instead:
    // keep the centre head pose on that same camera between eye renders so
    // the original wrist menu, body, quickbelt and movement read it too.
    public void UpdateGameplayHeadPose() {
        if (sourceCamera == null || trackingOrigin == null)
            return;
        XRBridge.Pose head;
        if (XRBridge.nh3vr_xr_head_pose(out head) != 0)
            return;
        sourceCamera.transform.position = trackingOrigin.TransformPoint(head.position);
        sourceCamera.transform.rotation = trackingOrigin.rotation * head.rotation;
    }
    void Update() {
        if (initialized)
            UpdateGameplayHeadPose();
    }
    void FixedUpdate() {
        if (initialized)
            UpdateGameplayHeadPose();
    }

    IEnumerator Start() {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
            throw new InvalidOperationException("Native H3VR requires the Metal renderer.");
        if (sourceCamera == null || trackingOrigin == null)
            throw new InvalidOperationException("Assign a game camera and tracking-space root.");
        uint size = (uint)Marshal.SizeOf(typeof(XRBridge.Frame));
        if (size != XRBridge.nh3vr_xr_frame_size())
            throw new InvalidOperationException("OpenXR frame ABI mismatch.");
        int started = useExistingSession ? 0
                                         : XRBridge.nh3vr_xr_start(loaderPath, runtimeManifest,
                                                                   syntheticHeadless ? 1u : 0u,
                                                                   eyeWidth, eyeHeight);
        if (started < 0) {
            string error = XRBridge.Error;
            XRBridge.nh3vr_xr_stop();
            throw new InvalidOperationException(error);
        }
        initialized = true;
        sourceWasEnabled = sourceCamera.enabled;
        sourceCamera.enabled = false;
        textures = new RenderTexture[2];
        var texturePointers = new IntPtr[2];
        NativeEyeTextureDiagnostics.PointerQueries = 0;
        NativeEyeTextureDiagnostics.CacheEnabled =
            Environment.GetEnvironmentVariable("NATIVEH3VR_CACHE_EYE_POINTERS") != "0";
        Debug.Log("NATIVE_GRAPHICS_POINTER_CACHE enabled=" +
                  NativeEyeTextureDiagnostics.CacheEnabled);
        IntPtr renderEvent = XRBridge.nh3vr_xr_render_event();
        var endOfFrame = new WaitForEndOfFrame();
        while (!stopping) {
            yield return endOfFrame;
            ulong completed;
            int status = XRBridge.nh3vr_xr_status(out completed);
            completedFrames = completed;
            XRBridge.Check(status);
            if (status == 1)
                continue;
            XRBridge.Frame frame;
            int result = XRBridge.nh3vr_xr_begin(out frame, size);
            XRBridge.Check(result);
            if (result != 0 || frame.shouldRender == 0)
                continue;
            latestFrame = frame;
            // RenderEye restores this centre pose, never the stale prefab
            // transform or the last eye's offset, after each render.
            UpdateGameplayHeadPose();
            try {
#if NEWENGINE_UNITY_2021
                NewEngineScopeActivation.BeginPair();
                try {
#endif
                    for (int i = 0; i < 2; i++) {
                        if (textures[i] != null && (textures[i].width != (int)frame.width ||
                                                    textures[i].height != (int)frame.height)) {
                            Destroy(textures[i]);
                            textures[i] = null;
                            texturePointers[i] = IntPtr.Zero;
                        }
                        if (textures[i] == null) {
                            var descriptor =
                                new RenderTextureDescriptor((int)frame.width, (int)frame.height);
                            descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB;
                            descriptor.depthBufferBits = 24;
                            descriptor.msaaSamples = 1;
                            textures[i] = new RenderTexture(descriptor);
                            textures[i].name =
                                i == 0 ? "Native Left Eye Color" : "Native Right Eye Color";
                        }
                        // A device loss can release an existing RenderTexture.
                        // Refresh its pointer after every actual recreation.
                        if (!textures[i].IsCreated()) {
                            if (!textures[i].Create())
                                throw new InvalidOperationException("Eye texture creation failed.");
                            texturePointers[i] = IntPtr.Zero;
                        }
                        XRBridge.View view = i == 0 ? frame.left : frame.right;
                        // Scene loading can temporarily remove the camera. Keep
                        // presenting the last completed eye image while the
                        // compositor's loading fade remains active.
                        if (!suspendSceneRendering && sourceCamera != null &&
                            trackingOrigin != null)
                            RenderEye(view, textures[i]);
                    }
#if NEWENGINE_UNITY_2021
                } finally {
                    NewEngineScopeActivation.EndPair();
                }
#endif
                for (int i = 0; i < 2; i++)
                    if (texturePointers[i] == IntPtr.Zero ||
                        !NativeEyeTextureDiagnostics.CacheEnabled) {
                        // Unity synchronizes its rendering thread for this
                        // API. The persistent eye targets retain their
                        // resources until recreation, not until each draw.
                        texturePointers[i] = textures[i].GetNativeTexturePtr();
                        NativeEyeTextureDiagnostics.PointerQueries++;
                        if (texturePointers[i] == IntPtr.Zero)
                            throw new InvalidOperationException(
                                "Eye texture has no native resource.");
                    }
                XRBridge.Check(
                    XRBridge.nh3vr_xr_set_textures(texturePointers[0], texturePointers[1]));
                GL.IssuePluginEvent(renderEvent, 1);
            } catch {
                Shutdown();
                throw;
            }
        }
        while (XRBridge.nh3vr_xr_stop() == 1)
            yield return null;
        initialized = false;
        ReleaseTextures();
    }
    public void RequestStop() { stopping = true; }
    void RenderEye(XRBridge.View view, RenderTexture target) {
        // Render the game's camera itself so its original image effects,
        // command buffers and render callbacks execute for each eye.
        // Camera.CopyFrom does not copy those attached components.
        var position = sourceCamera.transform.position;
        var rotation = sourceCamera.transform.rotation;
        var projection = sourceCamera.projectionMatrix;
        var nonJitteredProjection = sourceCamera.nonJitteredProjectionMatrix;
        bool jitteredTransparent = sourceCamera.useJitteredProjectionMatrixForTransparentRendering;
        var oldTarget = sourceCamera.targetTexture;
        var oldStereo = sourceCamera.stereoTargetEye;
        bool oldMSAA = sourceCamera.allowMSAA;
        try {
            sourceCamera.stereoTargetEye = StereoTargetEyeMask.None;
            sourceCamera.allowMSAA = false;
            sourceCamera.targetTexture = target;
            sourceCamera.transform.position = trackingOrigin.TransformPoint(view.pose.position);
            sourceCamera.transform.rotation = trackingOrigin.rotation * view.pose.rotation;
            sourceCamera.projectionMatrix =
                view.Projection(sourceCamera.nearClipPlane, sourceCamera.farClipPlane);
            NativeEyeProjection.Begin(sourceCamera, sourceCamera.projectionMatrix);
            try {
                sourceCamera.Render();
            } finally {
                NativeEyeProjection.End();
            }
        } finally {
            sourceCamera.transform.position = position;
            sourceCamera.transform.rotation = rotation;
            sourceCamera.projectionMatrix = projection;
            sourceCamera.nonJitteredProjectionMatrix = nonJitteredProjection;
            sourceCamera.useJitteredProjectionMatrixForTransparentRendering = jitteredTransparent;
            sourceCamera.targetTexture = oldTarget;
            sourceCamera.stereoTargetEye = oldStereo;
            sourceCamera.allowMSAA = oldMSAA;
        }
    }
    void OnGUI() {
        // Drawing a released target can implicitly recreate its resource
        // before the renderer notices the loss and invalidates its pointer.
        // Let the render coroutine own recreation and cache refresh.
        if (!mirrorToWindow || textures == null || textures[0] == null ||
            !textures[0].IsCreated() || Event.current.type != EventType.Repaint)
            return;
        int depth = GUI.depth;
        Color color = GUI.color;
        GUI.depth = 10000;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), textures[0],
                        ScaleMode.ScaleToFit, false);
        GUI.depth = depth;
        GUI.color = color;
    }
    void ReleaseTextures() {
        if (sourceCamera != null)
            sourceCamera.enabled = sourceWasEnabled;
        if (textures != null)
            foreach (RenderTexture texture in textures)
                if (texture != null)
                    Destroy(texture);
        textures = null;
    }
    void Shutdown() {
        if (!initialized)
            return;
        // The native plugin retains source textures until GPU completion.
        // Avoid waiting here: Unity may still need to commit its current buffer.
        int result = XRBridge.nh3vr_xr_stop();
        if (result == 1)
            Debug.LogWarning("OpenXR GPU submission pending; use RequestStop and allow its " +
                             "coroutine to drain before destroying renderer.");
        else
            initialized = false;
        ReleaseTextures();
    }
    void OnDestroy() { Shutdown(); }
}
}
