using System;
using UnityEngine;

namespace NativeH3VR
{
    public static class SyntheticHands
    {
        static readonly NativeTracking.Device[] Samples = new NativeTracking.Device[2];
        static readonly double[] Expires = new double[2];
        public static bool Active { get; private set; }

        public static void Begin()
        {
            if (Active || !XRBridge.IsRunning ||
                Environment.GetEnvironmentVariable("VR_OPENXR_SIMULATE") != "1" ||
                Environment.GetEnvironmentVariable("VR_OPENXR_HEADLESS") != "1" ||
                Environment.GetEnvironmentVariable("NATIVEH3VR_ACCEPTANCE_DIAGNOSTICS") != "1")
                throw new InvalidOperationException("Synthetic hands require an active headless run with acceptance diagnostics enabled.");
            Active = true;
            Debug.Log("NATIVEH3VR_SYNTHETIC_HANDS_BEGIN");
        }

        public static void End()
        {
            Active = false;
            Array.Clear(Samples, 0, Samples.Length);
            Array.Clear(Expires, 0, Expires.Length);
        }

        static int Index(uint role)
        {
            if (!Active) throw new InvalidOperationException("Synthetic hands are inactive.");
            if (role != 2 && role != 3) throw new ArgumentOutOfRangeException(nameof(role));
            return (int)role - 2;
        }

        static bool Unit(float value) => value >= 0 && value <= 1;
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static void SetHand(uint role, Vector3 position, Quaternion rotation, float trigger, float grip)
        {
            int index = Index(role);
            float norm = Quaternion.Dot(rotation, rotation);
            if (!Finite(position.sqrMagnitude) || !Finite(norm) || norm < .5f || !Unit(trigger) || !Unit(grip))
                throw new ArgumentException("Invalid synthetic hand sample.");
            var pose = new XRBridge.Pose { position = position, rotation = rotation.normalized };
            var axes = new float[16];
            axes[1] = trigger;
            axes[8] = grip;
            Samples[index] = new NativeTracking.Device
            {
                role = role,
                name = "Synthetic controller",
                flags = NativeTracking.Connected | NativeTracking.PoseValid | NativeTracking.PoseTracked | NativeTracking.InputCurrent,
                buttons = (trigger > .75f ? 1u : 0u) | (grip > .5f ? 128u : 0u),
                touches = (trigger > 0 ? 1u : 0u) | (grip > 0 ? 128u : 0u),
                trackingPose = pose,
                gripPose = pose,
                aimPose = pose,
                axes = axes,
            };
            Expires[index] = Time.realtimeSinceStartupAsDouble + 1;
        }

        public static void SetFingerProximity(uint role, float middle, float ring, float little, float indexFinger)
        {
            int index = Index(role);
            float[] axes = Samples[index].axes;
            if (axes == null || !Unit(middle) || !Unit(ring) || !Unit(little) || !Unit(indexFinger))
                throw new ArgumentException("A valid synthetic hand sample is required before setting finger curls.");
            axes[4] = middle;
            axes[5] = ring;
            axes[6] = little;
            axes[7] = indexFinger;
        }

        internal static bool TryHand(uint role, out NativeTracking.Device hand)
        {
            int index = (int)role - 2;
            hand = Samples[index];
            return Active && hand.axes != null && Time.realtimeSinceStartupAsDouble <= Expires[index];
        }
    }
}
