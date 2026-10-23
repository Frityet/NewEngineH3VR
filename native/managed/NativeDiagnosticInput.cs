using System;
using UnityEngine;
namespace NativeH3VR {
// Opt-in, process-local test input. Never reads or writes a hardware broker.
// Does not activate merely because the runtime uses synthetic head tracking.
public static class NativeDiagnosticInput {
    static bool active;
    static NativeTracking.Device[] hands = new NativeTracking.Device[2];
    static float[] updated = new float[2];
    public static bool Active {
        get { return active; }
    }
    public static void Begin() {
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") != "synthetic" ||
            Environment.GetEnvironmentVariable("NATIVEH3VR_DIAGNOSTIC_INPUT") != "1" ||
            !NativeSteamVRInput.Synthetic || !NativeSteamVRSystem.Running)
            throw new InvalidOperationException(
                "Diagnostic input requires an active synthetic session and explicit " +
                "NATIVEH3VR_DIAGNOSTIC_INPUT=1.");
        if (active)
            throw new InvalidOperationException("Diagnostic input is already active.");
        hands = new NativeTracking.Device[2];
        updated = new[] { -1000f, -1000f };
        active = true;
        Debug.LogWarning("NATIVE_DIAGNOSTIC_INPUT_BEGIN simulated controller " +
                         "poses/buttons/proximity only; no physical interaction proof.");
    }
    public static void SetHand(uint role, Vector3 position, Quaternion rotation, float trigger,
                               float grip, uint buttons = 0, uint touches = 0) {
        if (!active)
            throw new InvalidOperationException("Diagnostic input is not active.");
        if (role != 2 && role != 3)
            throw new ArgumentOutOfRangeException("role");
        if (float.IsNaN(position.sqrMagnitude) || float.IsInfinity(position.sqrMagnitude) ||
            float.IsNaN(rotation.x + rotation.y + rotation.z + rotation.w) ||
            float.IsInfinity(rotation.x + rotation.y + rotation.z + rotation.w) ||
            rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z +
                    rotation.w * rotation.w <
                .5f ||
            float.IsNaN(trigger) || float.IsNaN(grip) || trigger < 0 || trigger > 1 || grip < 0 ||
            grip > 1)
            throw new ArgumentException("Invalid diagnostic hand state.");
        var axes = new float[16];
        axes[1] = trigger;
        axes[8] = grip;
        var pose = new XRBridge.Pose { position = position, rotation = rotation.normalized };
        hands[role - 2] = new NativeTracking.Device {
            role = role,
            flags = NativeTracking.Connected | NativeTracking.PoseValid |
                    NativeTracking.PoseTracked | NativeTracking.InputCurrent,
            name = "Diagnostic hand",
            serial = role == 2 ? "DIAGNOSTIC-LEFT" : "DIAGNOSTIC-RIGHT",
            trackingPose = pose,
            gripPose = pose,
            aimPose = pose,
            axes = axes,
            buttons = buttons | (trigger > .75f ? 1u : 0u) | (grip > .5f ? 128u : 0u),
            touches = touches | (trigger > 0 ? 1u : 0u) | (grip > 0 ? 128u : 0u)
        };
        updated[role - 2] = Time.realtimeSinceStartup;
    }
    // Exact normalized raw channels used by the pinned Index broker.
    // Refresh SetHand first; this deliberately does not refresh sample age.
    public static void SetFingerProximity(uint role, float middle, float ring, float pinky,
                                          float index) {
        if (!active || role < 2 || role > 3 || hands[role - 2].axes == null)
            throw new InvalidOperationException(
                "Set a diagnostic hand before its proximity channels.");
        float[] values = { middle, ring, pinky, index };
        foreach (float v in values)
            if (float.IsNaN(v) || float.IsInfinity(v) || v < 0 || v > 1)
                throw new ArgumentException("Invalid diagnostic proximity.");
        for (int i = 0; i < 4; i++)
            hands[role - 2].axes[4 + i] = values[i];
    }
    internal static NativeTracking.Snapshot Read() {
        var devices = new NativeTracking.Device[2];
        for (int i = 0; i < 2; i++)
            if (Time.realtimeSinceStartup - updated[i] <= 1)
                devices[i] = hands[i];
        return new NativeTracking.Snapshot { abiVersion = 1, deviceCount = 2, devices = devices,
                                             floorState = 2,
                                             monotonicSeconds = Time.realtimeSinceStartup };
    }
    public static void End() {
        if (active)
            Debug.Log("NATIVE_DIAGNOSTIC_INPUT_END");
        active = false;
        hands = new NativeTracking.Device[2];
    }
}
}
