using System;
using System.Runtime.InteropServices;
using NativeH3VR;
using UnityEngine;

public static class IndexRawPose
{
    public static void Verify()
    {
        // Reference quaternions calculated from the left/right SteamVR Index
        // 1.0 render model transforms, expressed in Unity coordinates.
        CheckHand(2, new Quaternion(-.133942018f, .017555015f, .005040052f, .990820849f));
        CheckHand(3, new Quaternion(-.133942018f, -.017555015f, -.005040052f, .990820849f));
        Debug.Log("NATIVEH3VR_INDEX_RAW_POSE_PASS");
    }

    static void CheckHand(uint role, Quaternion rawToGrip)
    {
        var rawPosition = new Vector3(role == 2 ? -.4f : .4f, 1.6f, 2.2f);
        var rawRotation = Quaternion.Euler(25f, role == 2 ? 72f : -54f, 31f);
        var gripDisplacement = rawRotation * new Vector3(0f, -.015f, -.13f);
        var gripPosition = rawPosition + gripDisplacement;
        var gripRotation = rawRotation * rawToGrip;
        var aimPosition = new Vector3(.3f, 1.7f, 2.25f);
        var aimRotation = Quaternion.Euler(15f, -25f, -5f);
        var angularVelocity = new Vector3(2f, 3f, 5f);
        var rawVelocity = new Vector3(.3f, .4f, .5f);
        var gripVelocity = rawVelocity + Vector3.Cross(angularVelocity, gripDisplacement);
        var hand = new HandSample
        {
            flags = NativeTracking.Connected | NativeTracking.PoseValid | NativeTracking.VelocityValid,
            profile = MetalOpenXR.ControllerProfile.ValveIndex,
            grip = XrPose(gripPosition, gripRotation),
            aim = XrPose(aimPosition, aimRotation),
            velocity = XrPosition(gripVelocity),
            angularVelocity = new Vector3(-angularVelocity.x, -angularVelocity.y, angularVelocity.z)
        };
        var device = H3VRMetalBinding.DecodeHand(role, Decode(hand));
        CheckPose(device.trackingPose, rawPosition, rawRotation);
        CheckPose(device.gripPose, gripPosition, gripRotation);
        CheckPose(device.aimPose, aimPosition, aimRotation);
        CheckVector(device.linearVelocity, rawVelocity);
        CheckVector(device.angularVelocity, angularVelocity);

        hand.flags = NativeTracking.Connected;
        device = H3VRMetalBinding.DecodeHand(role, Decode(hand));
        CheckPose(device.trackingPose, gripPosition, gripRotation);

        hand.flags |= NativeTracking.PoseValid | NativeTracking.VelocityValid;
        hand.profile = MetalOpenXR.ControllerProfile.Unknown;
        device = H3VRMetalBinding.DecodeHand(role, Decode(hand));
        CheckPose(device.trackingPose, gripPosition, gripRotation);
        CheckVector(device.linearVelocity, gripVelocity);
    }

    static Vector3 XrPosition(Vector3 value) => new Vector3(value.x, value.y, -value.z);

    static PoseSample XrPose(Vector3 position, Quaternion rotation) => new PoseSample
    {
        position = XrPosition(position),
        rotation = new Quaternion(-rotation.x, -rotation.y, rotation.z, rotation.w)
    };

    static void CheckPose(XRBridge.Pose actual, Vector3 position, Quaternion rotation)
    {
        CheckVector(actual.position, position);
        if (Mathf.Abs(Quaternion.Dot(actual.rotation, rotation)) < .999999f)
            throw new InvalidOperationException($"Unexpected rotation {actual.rotation}, expected {rotation}");
    }

    static void CheckVector(Vector3 actual, Vector3 expected)
    {
        if (Vector3.Distance(actual, expected) > .00001f)
            throw new InvalidOperationException($"Unexpected vector {actual}, expected {expected}");
    }

    static MetalOpenXR.Hand Decode(HandSample sample)
    {
        int size = Marshal.SizeOf<HandSample>();
        if (size != Marshal.SizeOf<MetalOpenXR.Hand>())
            throw new InvalidOperationException("Unexpected OpenXR hand data layout");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(sample, buffer, false);
            return Marshal.PtrToStructure<MetalOpenXR.Hand>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PoseSample
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HandSample
    {
        public uint flags, buttons, touches;
        public MetalOpenXR.ControllerProfile profile;
        public PoseSample grip, aim;
        public Vector3 velocity, angularVelocity;
        public float trigger, squeeze, squeezeForce, trackpadForce;
        public float trackpadX, trackpadY, thumbstickX, thumbstickY;
    }
}
