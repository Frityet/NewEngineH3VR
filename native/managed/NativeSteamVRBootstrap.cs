using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEngine.Events;
using Valve.VR;
namespace NativeH3VR {
public static class NativeSteamVRBootstrap {
    static readonly Dictionary<Type, object> interfaces = new Dictionary<Type, object>();
    static readonly HashSet<string> warnings = new HashSet<string>();
    static NativeStereoRenderer renderer;
    public static bool SceneRenderingSuspended { get; private set; }
    public static void SetSceneRenderingSuspended(bool suspended) {
        SceneRenderingSuspended = suspended;
        if (renderer != null)
            renderer.suspendSceneRendering = suspended;
    }
#if NEWENGINE_UNITY_2021
    // OpenXR is a launch/session choice. Refresh when creating a new session,
    // rather than allocating environment strings in every guarded input call.
    // A nested cache leaves the existing bootstrap field layout unchanged.
    static class LaunchConfiguration {
        internal static string mode = Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR");
        internal static string Refresh() {
            return mode = Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR");
        }
        internal static bool Enabled {
            get { return mode == "synthetic" || mode == "live"; }
        }
    }
    public static bool Enabled {
        get { return LaunchConfiguration.Enabled; }
    }
#else
    public static bool Enabled {
        get {
            return Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") == "synthetic" ||
                   Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") == "live";
        }
    }
#endif
    [DllImport("NativeH3VRXR")] static extern IntPtr nh3vr_xr_system_name();
    public static string HeadsetModel {
        get {
            string actual = Marshal.PtrToStringAnsi(nh3vr_xr_system_name());
            if (string.IsNullOrEmpty(actual))
                return "";
            return NativeSteamVRInput.Synthetic ? actual + " (Index diagnostic profile)" : actual;
        }
    }
    // Old game code consults Unity VRDevice.model, which is empty without
    // Unity's removed OpenVR provider. Resolve against our active runtime.
    public static string ResolveLegacyHeadsetModel(string engineModel) {
        if (!Enabled)
            return engineModel;
        if (!NativeSteamVRSystem.Running && SteamVR.instance == null)
            return engineModel;
        return HeadsetModel;
    }
    public static string RuntimeRoot {
        get {
            string configured = Environment.GetEnvironmentVariable("NATIVEH3VR_RUNTIME");
            if (configured != null)
                return configured;
            string current = Path.Combine(Application.streamingAssetsPath, "NewEngine");
            // Read-only fallback for already-built local players. New staging
            // always produces NewEngine; serialized assembly identity stays put.
            string legacy = Path.Combine(Application.streamingAssetsPath, "NativeRuntime");
            return Directory.Exists(current) || !Directory.Exists(legacy) ? current : legacy;
        }
    }
    public static T Interface<T>()
        where T : class {
        object value;
        if (!interfaces.TryGetValue(typeof(T), out value)) {
            value = FormatterServices.GetUninitializedObject(typeof(T));
            interfaces[typeof(T)] = value;
        }
        return (T)value;
    }
    public static bool ForceTubeUnavailable() {
        if (Application.platform != RuntimePlatform.OSXPlayer &&
            Application.platform != RuntimePlatform.OSXEditor)
            return false;
        Unsupported("ForceTube gunstock support is disabled on macOS: its bundled plugin is " +
                    "Windows-only; no gunstock commands are sent");
        return true;
    }
    public static bool LIVUnavailable() {
        if (Application.platform != RuntimePlatform.OSXPlayer &&
            Application.platform != RuntimePlatform.OSXEditor)
            return false;
        Unsupported("LIV mixed-reality capture is disabled on macOS: the bundled LIV_MR " +
                    "shared-texture plugin has no native Metal implementation");
        return true;
    }
    public static void DisableLIV(Behaviour component) { component.enabled = false; }
    public static void Unsupported(string feature) {
        if (warnings.Add(feature))
            Debug.LogWarning("Native H3VR feature unavailable: " + feature);
    }
    static void Set(object instance, string field, object value) {
        Type type = typeof(SteamVR);
        FieldInfo info =
            type.GetField(field, BindingFlags.Static | BindingFlags.Instance |
                                     BindingFlags.NonPublic | BindingFlags.Public) ??
            type.GetField("<" + field + ">k__BackingField",
                          BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
        if (info == null)
            throw new MissingFieldException(type.FullName, field);
        info.SetValue(instance, value);
    }
    static T Listener<T>(SteamVR value, string method)
        where T : class {
        return Delegate.CreateDelegate(
            typeof(T), value,
            typeof(SteamVR).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)) as T;
    }
    public static SteamVR Create() {
        try {
            return CreateCore();
        } catch (Exception error) {
            NativeSteamVRSystem.Running = false;
            XRBridge.nh3vr_xr_stop();
            Set(null, "_instance", null);
            Set(null, "_enabled", false);
            SteamVR.initializedState = SteamVR.InitializedStates.InitializeFailure;
            SteamVR_Events.Initialized.Send(false);
            Debug.LogException(error);
            return null;
        }
    }
    static SteamVR CreateCore() {
#if NEWENGINE_UNITY_2021
        bool synthetic = LaunchConfiguration.Refresh() == "synthetic";
#else
        bool synthetic = Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") == "synthetic";
#endif
        NativeSteamVRInput.Synthetic = synthetic;
        XRBridge.Check(
            XRBridge.nh3vr_xr_start(Path.Combine(RuntimeRoot, "build/bin/libopenxr_loader.dylib"),
                                    Path.Combine(RuntimeRoot, "build/openxr/virtual-reality.json"),
                                    synthetic ? 1u : 0u, 1440, 1600));
        XRBridge.Frame frame;
        uint size = XRBridge.nh3vr_xr_frame_size();
        XRBridge.Check(XRBridge.nh3vr_xr_begin(out frame, size));
        XRBridge.Check(XRBridge.nh3vr_xr_cancel());
        XRBridge.Check(XRBridge.nh3vr_xr_eye_views(out NativeSteamVRSystem.LeftEye,
                                                   out NativeSteamVRSystem.RightEye));
        NativeSteamVRSystem.Width = frame.width;
        NativeSteamVRSystem.Height = frame.height;
        NativeSteamVRSystem.Running = true;
        var value = Interface<SteamVR>();
        Set(null, "_instance", value);
        Set(null, "_enabled", true);
        Set(null, "settings", SteamVR_Settings.instance);
        Set(value, "hmd", Interface<CVRSystem>());
        Set(value, "compositor", Interface<CVRCompositor>());
        Set(value, "overlay", Interface<CVROverlay>());
        Set(value, "sceneWidth", (float)frame.width);
        Set(value, "sceneHeight", (float)frame.height);
        var left = NativeSteamVRSystem.LeftEye;
        var right = NativeSteamVRSystem.RightEye;
        float halfX = Mathf.Max(-Mathf.Tan(left.fovLeft), Mathf.Tan(left.fovRight),
                                -Mathf.Tan(right.fovLeft), Mathf.Tan(right.fovRight));
        float halfY = Mathf.Max(Mathf.Tan(left.fovUp), -Mathf.Tan(left.fovDown),
                                Mathf.Tan(right.fovUp), -Mathf.Tan(right.fovDown));
        Set(value, "tanHalfFov", new Vector2(halfX, halfY));
        Set(value, "aspect", halfX / halfY);
        Set(value, "fieldOfView", 2 * Mathf.Atan(halfY) * Mathf.Rad2Deg);
        Set(value, "textureBounds",
            new[] { new VRTextureBounds_t { uMax = 1, vMax = 1 },
                    new VRTextureBounds_t { uMax = 1, vMax = 1 } });
        Set(value, "eyes",
            new[] { new SteamVR_Utils.RigidTransform(left.pose.position, left.pose.rotation),
                    new SteamVR_Utils.RigidTransform(right.pose.position, right.pose.rotation) });
        value.textureType = ETextureType.Metal;
        SteamVR_Events.Initializing.Listen(Listener<UnityAction<bool>>(value, "OnInitializing"));
        SteamVR_Events.Calibrating.Listen(Listener<UnityAction<bool>>(value, "OnCalibrating"));
        SteamVR_Events.OutOfRange.Listen(Listener<UnityAction<bool>>(value, "OnOutOfRange"));
        SteamVR_Events.DeviceConnected.Listen(
            Listener<UnityAction<int, bool>>(value, "OnDeviceConnected"));
        SteamVR_Events.NewPoses.Listen(
            Listener<UnityAction<TrackedDevicePose_t[]>>(value, "OnNewPoses"));
        string manifest = Environment.GetEnvironmentVariable("NATIVEH3VR_ACTIONS") ??
                          Path.Combine(Application.streamingAssetsPath, "SteamVR/actions.json");
        var error = NativeSteamVRInput.SetActionManifestPath(manifest);
        if (error != EVRInputError.None)
            throw new InvalidOperationException("Native SteamVR action manifest: " + error + " " +
                                                manifest);
        SteamVR_Settings.instance.trackingSpace = ETrackingUniverseOrigin.TrackingUniverseStanding;
        SteamVR_Input.Initialize();
        // The native adapter supplies estimated curl summaries and the
        // controller root pose. Existing game hands consume these values;
        // do not request nonexistent tracked bone transforms every frame.
        foreach (var skeleton in SteamVR_Input.actionsSkeleton)
            skeleton.onlyUpdateSummaryData = true;
        SteamVR.initializedState = SteamVR.InitializedStates.InitializeSuccess;
        SteamVR_Events.Initialized.Send(true);
        Debug.Log("NATIVE_STEAMVR_INITIALIZED backend=Metal/OpenXR synthetic=" + synthetic +
                  " actionCount=" + SteamVR_Input.actions.Length);
        return value;
    }
    public static void CameraEnabled(SteamVR_Camera camera) {
        if (!Enabled || camera == null)
            return;
        if (!NativeSteamVRSystem.Running) {
            var instance = SteamVR.instance;
            if (instance == null)
                return;
        }
        Transform origin = camera.head != null ? camera.origin : camera.transform.parent;
        if (origin == null)
            throw new InvalidOperationException("SteamVR camera has no tracking-space origin.");
        if (renderer == null) {
            // The OpenXR session belongs to the application. A scene's
            // camera can be destroyed/recreated without ending that session.
            var host = new GameObject("Native H3VR stereo session");
            UnityEngine.Object.DontDestroyOnLoad(host);
            renderer = host.AddComponent<NativeStereoRenderer>();
            renderer.syntheticHeadless = NativeSteamVRInput.Synthetic;
            renderer.useExistingSession = true;
            renderer.suspendSceneRendering = SceneRenderingSuspended;
        }
        renderer.BindCamera(camera.GetComponent<Camera>(), origin);
        Debug.Log("NATIVE_STEAMVR_CAMERA " + camera.name + " origin=" + origin.name);
    }
    public static bool IsHmdPresent() { return NativeSteamVRSystem.Running; }
    public static bool IsRuntimeInstalled() {
        return File.Exists(Path.Combine(RuntimeRoot, "build/bin/libVirtualRealityOpenXR.dylib"));
    }
    public static string GetStringForHmdError(EVRInitError error) { return error.ToString(); }
}
}
