using System;
using System.Runtime.InteropServices;
using UnityEngine;
namespace NativeH3VR {
public static class NativeTracking {
    public const uint Connected = 1, PoseValid = 2, PoseTracked = 4, VelocityValid = 8,
                      InputCurrent = 16, ControllerOffsets = 64;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Device {
        public uint role, flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string serial;
        public XRBridge.Pose trackingPose, gripPose, aimPose;
        public Vector3 linearVelocity, angularVelocity;
        public double poseAgeSeconds, opticalAgeSeconds;
        public uint buttons, touches;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public float[] axes;
        public float Axis(int index) { return axes == null ? 0 : axes[index]; }
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Snapshot {
        public uint abiVersion, structSize, flags, deviceCount, floorState, reserved;
        public double monotonicSeconds, floorY;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public Device[] devices;
        public Device Role(uint role) {
            if (devices != null)
                for (int i = 0; i < deviceCount && i < devices.Length; i++)
                    if (devices[i].role == role)
                        return devices[i];
            return new Device { role = role };
        }
    }
    [DllImport("NativeH3VRTracking", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint nh3vr_tracking_snapshot_size();
    [DllImport("NativeH3VRTracking", CallingConvention = CallingConvention.Cdecl)]
    static extern int nh3vr_read_tracking(string socket, out Snapshot snapshot, uint size);
    public static int Read(out Snapshot snapshot) {
        uint size = (uint)Marshal.SizeOf(typeof(Snapshot));
        if (size != nh3vr_tracking_snapshot_size())
            throw new InvalidOperationException("Tracking snapshot ABI mismatch.");
        return nh3vr_read_tracking(null, out snapshot, size);
    }
}
}
