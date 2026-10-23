using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Valve.VR;
using Valve.Newtonsoft.Json.Linq;
namespace NativeH3VR {
// Adapts the installed game's action manifest/Knuckles binding to actual
// broker inputs. Not an OpenVR function-table implementation.
public static class NativeSteamVRInput {
    static readonly Dictionary<string, ulong> handles =
        new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<ulong, string> paths = new Dictionary<ulong, string>();
    static readonly Dictionary<ulong, string> types = new Dictionary<ulong, string>();
    static ulong next = 100;
    static readonly Dictionary<ulong, uint> skeletonRoles = new Dictionary<ulong, uint>();
    static bool announcedEstimatedSkeleton;
    static NativeTracking.Snapshot current, previous;
    static readonly Dictionary<ulong, HashSet<ulong>> activeSets =
        new Dictionary<ulong, HashSet<ulong>>();
    static bool fixture;
    static bool fixtureFocus = true;
    static bool[] awaitingNeutral = { true, true };
    static int lastRefreshFrame = -1;
    public static bool Synthetic { get; set; }
    static ulong Register(string path, string type) {
        ulong handle;
        if (!handles.TryGetValue(path, out handle)) {
            handle = next++;
            handles[path] = handle;
            paths[handle] = path.ToLowerInvariant();
            types[handle] = type;
        }
        return handle;
    }
    public static EVRInputError SetActionManifestPath(string path) {
        if (!File.Exists(path))
            return EVRInputError.MismatchedActionManifest;
        try {
            JObject manifest = JObject.Parse(File.ReadAllText(path));
            foreach (JToken action in manifest["actions"]) {
                ulong handle = Register((string)action["name"], (string)action["type"]);
                if ((string)action["type"] == "skeleton") {
                    string skeleton = (string)action["skeleton"];
                    skeletonRoles[handle] = skeleton == "/skeleton/hand/left"    ? 2u
                                            : skeleton == "/skeleton/hand/right" ? 3u
                                                                                 : 0u;
                }
            }
            foreach (JToken set in manifest["action_sets"])
                Register((string)set["name"], "set");
            return EVRInputError.None;
        } catch {
            return EVRInputError.MismatchedActionManifest;
        }
    }
    static EVRInputError Handle(string name, string kind, ref ulong output) {
        output = 0;
        ulong handle;
        if (name == null || !handles.TryGetValue(name, out handle))
            return EVRInputError.NameNotFound;
        if (kind != null && types[handle] != kind)
            return EVRInputError.WrongType;
        output = handle;
        return EVRInputError.None;
    }
    public static EVRInputError GetActionHandle(string name, ref ulong output) {
        return Handle(name, null, ref output);
    }
    public static EVRInputError GetActionSetHandle(string name, ref ulong output) {
        return Handle(name, "set", ref output);
    }
    public static EVRInputError GetInputSourceHandle(string path, ref ulong output) {
        if (string.IsNullOrEmpty(path) || path == "/unrestricted") {
            output = 0;
            return EVRInputError.None;
        }
        if (!path.StartsWith("/user/")) {
            output = 0;
            return EVRInputError.NameNotFound;
        }
        output = Register(path, "source");
        return EVRInputError.None;
    }
    public static NativeTracking.Snapshot Snapshot {
        get { return current; }
    }
    public static bool InputFocused {
        get {
            return fixture ? fixtureFocus
                           : NativeSteamVRSystem.Running && XRBridge.nh3vr_xr_focused() != 0;
        }
    }
    static NativeTracking.Snapshot GateInput(NativeTracking.Snapshot sample, bool focused) {
        if (!focused) {
            awaitingNeutral[0] = awaitingNeutral[1] = true;
        }
        if (sample.devices == null)
            return sample;
        sample.devices = (NativeTracking.Device[])sample.devices.Clone();
        for (int i = 0; i < sample.devices.Length && i < sample.deviceCount; i++) {
            var device = sample.devices[i];
            bool allow = focused;
            if (device.role == 2 || device.role == 3) {
                int hand = (int)device.role - 2;
                // Do not turn controls held in the dashboard into new game
                // presses. Require a current neutral sample per hand first.
                if (focused && awaitingNeutral[hand] &&
                    (device.flags & NativeTracking.InputCurrent) != 0 && device.buttons == 0 &&
                    Mathf.Abs(device.Axis(1)) < .05f && Mathf.Abs(device.Axis(8)) < .05f &&
                    Mathf.Abs(device.Axis(9)) < .05f)
                    awaitingNeutral[hand] = false;
                allow = focused && !awaitingNeutral[hand];
            }
            if (!allow) {
                device.flags &= ~NativeTracking.InputCurrent;
                device.buttons = device.touches = 0;
                device.axes = new float[16];
                sample.devices[i] = device;
            }
        }
        return sample;
    }
    public static void RefreshTracking() {
        if (fixture)
            return;
        if (Time.frameCount == lastRefreshFrame)
            return;
        lastRefreshFrame = Time.frameCount;
        previous = current;
        NativeTracking.Snapshot raw;
        if (Synthetic)
            raw = NativeDiagnosticInput.Active ? NativeDiagnosticInput.Read()
                                               : new NativeTracking.Snapshot();
        else if (NativeTracking.Read(out raw) != 0)
            raw = new NativeTracking.Snapshot();
        current = GateInput(raw, InputFocused);
    }
    // Used only by deterministic adapter tests; cannot activate in live mode.
    internal static void SetFixture(NativeTracking.Snapshot before, NativeTracking.Snapshot after) {
        if (!Synthetic)
            throw new InvalidOperationException("Fixtures require explicit synthetic mode.");
        fixture = true;
        fixtureFocus = true;
        previous = before;
        current = after;
    }
    internal static void SetFocusFixture(NativeTracking.Snapshot sample, bool focused) {
        if (!Synthetic)
            throw new InvalidOperationException("Fixtures require explicit synthetic mode.");
        fixture = true;
        fixtureFocus = focused;
        previous = current;
        current = GateInput(sample, focused);
    }
    internal static void EndFixture() {
        fixture = false;
        fixtureFocus = true;
        current = previous = new NativeTracking.Snapshot();
        awaitingNeutral[0] = awaitingNeutral[1] = true;
        lastRefreshFrame = -1;
    }
    public static EVRInputError UpdateActionState(VRActiveActionSet_t[] sets, uint size) {
        if (size != (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t)) || sets == null)
            return EVRInputError.InvalidParam;
        activeSets.Clear();
        foreach (var set in sets) {
            if (!types.ContainsKey(set.ulActionSet) || types[set.ulActionSet] != "set")
                return EVRInputError.InvalidHandle;
            HashSet<ulong> sources;
            if (!activeSets.TryGetValue(set.ulActionSet, out sources)) {
                sources = new HashSet<ulong>();
                activeSets[set.ulActionSet] = sources;
            }
            sources.Add(set.ulRestrictedToDevice);
        }
        if (!fixture)
            RefreshTracking();
        return EVRInputError.None;
    }
    static uint Role(ulong source) {
        string path;
        if (!paths.TryGetValue(source, out path))
            return 0;
        return path == "/user/hand/left"    ? 2u
               : path == "/user/hand/right" ? 3u
               : path == "/user/head"       ? 1u
                                            : 0u;
    }
    static ulong Source(uint role) {
        return Register(role == 2   ? "/user/hand/left"
                        : role == 3 ? "/user/hand/right"
                                    : "/user/head",
                        "source");
    }
    static EVRInputError Resolve(ulong action, string type, ulong source, out string name,
                                 out NativeTracking.Device device, out NativeTracking.Device old,
                                 out ulong origin, out bool enabled) {
        name = null;
        device = old = default(NativeTracking.Device);
        origin = 0;
        enabled = false;
        if (!paths.ContainsKey(action))
            return EVRInputError.InvalidHandle;
        if (types[action] != type)
            return EVRInputError.WrongType;
        string path = paths[action];
        name = path.Substring(path.LastIndexOf('/') + 1);
        string setPath = path.Substring(0, path.IndexOf('/', 9));
        ulong set;
        HashSet<ulong> allowed;
        if (!handles.TryGetValue(setPath, out set) || !activeSets.TryGetValue(set, out allowed))
            return EVRInputError.None;
        uint role = Role(source);
        if (source == 0) {
            // OpenVR Any aggregates an active origin. Prefer a current left
            // controller, then right; do not fabricate an absent device.
            role = (current.Role(2).flags & NativeTracking.InputCurrent) != 0 ? 2u : 3u;
            if (name == "headsetonhead")
                role = 1;
        }
        if (role == 0)
            return EVRInputError.None;
        origin = Source(role);
        if (!allowed.Contains(0) && !allowed.Contains(origin))
            return EVRInputError.None;
        device = current.Role(role);
        old = previous.Role(role);
        enabled = (device.flags & NativeTracking.Connected) != 0;
        return EVRInputError.None;
    }
    static bool Digital(string name, NativeTracking.Device device, out bool supported) {
        supported = true;
        uint bits = device.buttons;
        switch (name) {
        case "trigger_button":
            return (bits & 1) != 0;
        case "trigger_touch":
            return (device.touches & 1) != 0;
        case "primary2axis_button":
            return (bits & 2) != 0 || device.Axis(9) > .5f;
        case "primary2axis_touch":
            return (device.touches & 2) != 0;
        case "secondary2axis_button":
            return (bits & 4) != 0;
        case "secondary2axis_touch":
            return (device.touches & 4) != 0;
        case "a_button":
            return (bits & 16) != 0;
        case "b_button":
            return (bits & 32) != 0;
        case "grip_button":
            return (bits & 128) != 0 || device.Axis(8) > .5f;
        case "grip_touch":
            return (device.touches & 128) != 0;
        case "headsetonhead":
            return device.role == 1 && device.Axis(1) > .5f;
        default:
            supported = false;
            return false;
        }
    }
    public static EVRInputError
    GetDigitalActionData(ulong action, ref InputDigitalActionData_t data, uint size, ulong source) {
        data = default(InputDigitalActionData_t);
        if (size != (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t)))
            return EVRInputError.InvalidParam;
        string name;
        NativeTracking.Device device, old;
        ulong origin;
        bool enabled;
        var error = Resolve(action, "boolean", source, out name, out device, out old, out origin,
                            out enabled);
        if (error != EVRInputError.None)
            return error;
        bool supported;
        bool value = Digital(name, device, out supported);
        if (!supported)
            return EVRInputError.NameNotFound;
        data.bActive = enabled && (device.flags & NativeTracking.InputCurrent) != 0;
        data.activeOrigin = data.bActive ? origin : 0;
        data.bState = data.bActive && value;
        bool oldValue =
            (old.flags & NativeTracking.InputCurrent) != 0 && Digital(name, old, out supported);
        data.bChanged = data.bState != oldValue;
        data.fUpdateTime = 0;
        return EVRInputError.None;
    }
    static Vector3 Analog(string name, NativeTracking.Device device, out bool supported) {
        supported = true;
        switch (name) {
        case "trigger_axis":
            return new Vector3(device.Axis(1), 0, 0);
        case "grip_squeeze":
            return new Vector3(device.Axis(8), 0, 0);
        case "thumb_squeeze":
            return new Vector3(device.Axis(9), 0, 0);
        case "primary2axis_axes":
            return new Vector3(device.Axis(2), device.Axis(3), 0);
        case "secondary2axis_axes":
            return new Vector3(device.Axis(10), device.Axis(11), 0);
        default:
            supported = false;
            return Vector3.zero;
        }
    }
    public static EVRInputError GetAnalogActionData(ulong action, ref InputAnalogActionData_t data,
                                                    uint size, ulong source) {
        data = default(InputAnalogActionData_t);
        if (size != (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t)))
            return EVRInputError.InvalidParam;
        string type;
        if (!types.TryGetValue(action, out type))
            return EVRInputError.InvalidHandle;
        if (type != "vector1" && type != "vector2" && type != "vector3")
            return EVRInputError.WrongType;
        string name;
        NativeTracking.Device device, old;
        ulong origin;
        bool enabled;
        var error =
            Resolve(action, type, source, out name, out device, out old, out origin, out enabled);
        if (error != EVRInputError.None)
            return error;
        bool supported;
        Vector3 value = Analog(name, device, out supported);
        if (!supported)
            return EVRInputError.NameNotFound;
        data.bActive = enabled && (device.flags & NativeTracking.InputCurrent) != 0;
        data.activeOrigin = data.bActive ? origin : 0;
        if (!data.bActive)
            value = Vector3.zero;
        Vector3 before = (old.flags & NativeTracking.InputCurrent) != 0
                             ? Analog(name, old, out supported)
                             : Vector3.zero;
        data.x = value.x;
        data.y = value.y;
        data.z = value.z;
        data.deltaX = value.x - before.x;
        data.deltaY = value.y - before.y;
        data.deltaZ = value.z - before.z;
        return EVRInputError.None;
    }
    public static TrackedDevicePose_t Pose(NativeTracking.Device device,
                                           ETrackingUniverseOrigin origin) {
        var output = new TrackedDevicePose_t();
        output.bDeviceIsConnected = (device.flags & NativeTracking.Connected) != 0;
        output.bPoseIsValid = (device.flags & NativeTracking.PoseValid) != 0;
        output.eTrackingResult =
            output.bPoseIsValid ? ETrackingResult.Running_OK : ETrackingResult.Uninitialized;
        if (!output.bPoseIsValid)
            return output;
        // H3VR's Knuckles binding explicitly requests pose/raw, so use the
        // calibrated tracking origin, not the grip/aim offset.
        var pose = device.trackingPose;
        if (origin == ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated &&
            current.floorState == 2)
            pose.position.y += (float)current.floorY;
        Matrix4x4 matrix = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
        output.mDeviceToAbsoluteTracking =
            new HmdMatrix34_t { m0 = matrix.m00,  m1 = matrix.m01,  m2 = -matrix.m02,
                                m3 = matrix.m03,  m4 = matrix.m10,  m5 = matrix.m11,
                                m6 = -matrix.m12, m7 = matrix.m13,  m8 = -matrix.m20,
                                m9 = -matrix.m21, m10 = matrix.m22, m11 = -matrix.m23 };
        if ((device.flags & NativeTracking.VelocityValid) != 0) {
            output.vVelocity =
                new HmdVector3_t { v0 = device.linearVelocity.x, v1 = device.linearVelocity.y,
                                   v2 = -device.linearVelocity.z };
            output.vAngularVelocity =
                new HmdVector3_t { v0 = -device.angularVelocity.x, v1 = -device.angularVelocity.y,
                                   v2 = device.angularVelocity.z };
        }
        return output;
    }
    public static EVRInputError GetPoseActionDataForNextFrame(ulong action,
                                                              ETrackingUniverseOrigin origin,
                                                              ref InputPoseActionData_t data,
                                                              uint size, ulong source) {
        data = default(InputPoseActionData_t);
        if (size != (uint)Marshal.SizeOf(typeof(InputPoseActionData_t)) ||
            origin == ETrackingUniverseOrigin.TrackingUniverseSeated)
            return EVRInputError.InvalidParam;
        string actionType;
        if (types.TryGetValue(action, out actionType) && actionType == "skeleton") {
            NativeTracking.Device hand;
            ulong handOrigin;
            bool active;
            var skeletalError = ResolveSkeleton(action, out hand, out handOrigin, out active);
            if (skeletalError != EVRInputError.None)
                return skeletalError;
            if (source != 0 && source != handOrigin)
                active = false;
            data.bActive = active;
            data.activeOrigin = active ? handOrigin : 0;
            if (active)
                data.pose = Pose(hand, origin);
            return EVRInputError.None;
        }
        string name;
        NativeTracking.Device device, old;
        ulong activeOrigin;
        bool enabled;
        var error = Resolve(action, "pose", source, out name, out device, out old, out activeOrigin,
                            out enabled);
        if (error != EVRInputError.None)
            return error;
        if (name != "pose")
            return EVRInputError.NoData;
        data.bActive = enabled;
        data.activeOrigin = enabled ? activeOrigin : 0;
        data.pose = Pose(device, origin);
        return EVRInputError.None;
    }
    public static EVRInputError
    GetPoseActionDataRelativeToNow(ulong action, ETrackingUniverseOrigin origin, float prediction,
                                   ref InputPoseActionData_t data, uint size, ulong source) {
        if (prediction != 0) {
            data = default(InputPoseActionData_t);
            return EVRInputError.NoData;
        }
        return GetPoseActionDataForNextFrame(action, origin, ref data, size, source);
    }
    public static EVRInputError GetOriginTrackedDeviceInfo(ulong origin, ref InputOriginInfo_t info,
                                                           uint size) {
        info = default(InputOriginInfo_t);
        uint role = Role(origin);
        if (size != (uint)Marshal.SizeOf(typeof(InputOriginInfo_t)))
            return EVRInputError.InvalidParam;
        if (role == 0)
            return EVRInputError.InvalidHandle;
        info.devicePath = origin;
        info.trackedDeviceIndex = role == 1 ? 0u : role == 2 ? 1u : 2u;
        return EVRInputError.None;
    }
    public static EVRInputError TriggerHapticVibrationAction(ulong action, float start,
                                                             float duration, float frequency,
                                                             float amplitude, ulong source) {
        string type;
        if (!types.TryGetValue(action, out type))
            return EVRInputError.InvalidHandle;
        if (type != "vibration")
            return EVRInputError.WrongType;
        if (start != 0 || duration <= 0 || duration > 10 || frequency < 0 || amplitude < 0 ||
            amplitude > 1 || float.IsNaN(amplitude) || float.IsNaN(frequency) ||
            float.IsNaN(duration))
            return EVRInputError.InvalidParam;
        uint role = Role(source);
        if (role != 2 && role != 3)
            return EVRInputError.InvalidDevice;
        if ((current.Role(role).flags & NativeTracking.InputCurrent) == 0)
            return EVRInputError.NoData;
        return XRBridge.nh3vr_xr_haptic(role, amplitude, frequency, duration) == 0
                   ? EVRInputError.None
                   : EVRInputError.IPCError;
    }
    static EVRInputError ResolveSkeleton(ulong action, out NativeTracking.Device device,
                                         out ulong origin, out bool active) {
        device = default(NativeTracking.Device);
        origin = 0;
        active = false;
        string kind;
        if (!types.TryGetValue(action, out kind))
            return EVRInputError.InvalidHandle;
        if (kind != "skeleton")
            return EVRInputError.WrongType;
        uint role;
        if (!skeletonRoles.TryGetValue(action, out role) || role == 0)
            return EVRInputError.InvalidSkeleton;
        string name;
        NativeTracking.Device previousDevice;
        var error = Resolve(action, "skeleton", Source(role), out name, out device,
                            out previousDevice, out origin, out active);
        active = active && (device.flags & NativeTracking.InputCurrent) != 0;
        if (active && !announcedEstimatedSkeleton) {
            announcedEstimatedSkeleton = true;
            Debug.LogWarning("NATIVE_ESTIMATED_SKELETON active: measured finger proximity plus " +
                             "trigger/grip/contact estimates; summary and controller root pose " +
                             "only, no measured bone joints or splay.");
        }
        return error;
    }
    public static EVRInputError
    GetSkeletalActionData(ulong action, ref InputSkeletalActionData_t data, uint size) {
        data = default(InputSkeletalActionData_t);
        if (size != (uint)Marshal.SizeOf(typeof(InputSkeletalActionData_t)))
            return EVRInputError.InvalidParam;
        NativeTracking.Device hand;
        ulong origin;
        bool active;
        var error = ResolveSkeleton(action, out hand, out origin, out active);
        data.bActive = active;
        data.activeOrigin = active ? origin : 0;
        return error;
    }
    public static EVRInputError GetSkeletalSummaryData(ulong action, EVRSummaryType summaryType,
                                                       ref VRSkeletalSummaryData_t summary) {
        summary = default(VRSkeletalSummaryData_t);
        if (summaryType != EVRSummaryType.FromDevice && summaryType != EVRSummaryType.FromAnimation)
            return EVRInputError.InvalidParam;
        NativeTracking.Device hand;
        ulong origin;
        bool active;
        var error = ResolveSkeleton(action, out hand, out origin, out active);
        if (error != EVRInputError.None)
            return error;
        if (!active)
            return EVRInputError.NoData;
        summary = NativeEstimatedHand.Summary(hand);
        return EVRInputError.None;
    }
    public static void ClearInactiveSkeletonSummary(SteamVR_Action_Skeleton_Source source) {
        // Original SteamVR caches the last curls when a binding disappears.
        // Native samples expire; retaining a closed curl would keep the
        // original Index grab state latched after loss of fresh input.
        if (!NativeSteamVRBootstrap.Enabled || source.active)
            return;
        Array.Clear(source.fingerCurls, 0, source.fingerCurls.Length);
        Array.Clear(source.fingerSplays, 0, source.fingerSplays.Length);
    }
    public static EVRInputError GetSkeletalTrackingLevel(ulong action,
                                                         ref EVRSkeletalTrackingLevel level) {
        level = EVRSkeletalTrackingLevel.VRSkeletalTracking_Estimated;
        NativeTracking.Device hand;
        ulong origin;
        bool active;
        return ResolveSkeleton(action, out hand, out origin, out active);
    }
}
}
