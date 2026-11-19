using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NativeH3VR
{
    public static class MetalOpenXR
    {
        const string Library = "NativeH3VRMetal";

        [StructLayout(LayoutKind.Sequential)]
        public struct Pose
        {
            Vector3 position;
            Quaternion rotation;

            public Vector3 Position => new Vector3(position.x, position.y, -position.z);
            public Quaternion Rotation => new Quaternion(-rotation.x, -rotation.y, rotation.z, rotation.w);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct View
        {
            public Pose pose;
            public float left, right, up, down;

            public Matrix4x4 Projection(float near, float far)
            {
                return Matrix4x4.Frustum(Mathf.Tan(left) * near, Mathf.Tan(right) * near,
                                         Mathf.Tan(down) * near, Mathf.Tan(up) * near, near, far);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Frame
        {
            public ulong sequence;
            public long displayTime, displayPeriod;
            public uint width, height, focused, reserved;
            public Pose head;
            public View left, right;
        }

        public enum ControllerProfile : uint { Unknown, ValveIndex }

        [StructLayout(LayoutKind.Sequential)]
        public struct Hand
        {
            public uint flags, buttons, touches;
            public ControllerProfile profile;
            public Pose grip, aim;
            Vector3 velocity, angularVelocity;
            public float trigger, squeeze, squeezeForce, trackpadForce;
            public float trackpadX, trackpadY, thumbstickX, thumbstickY;

            public Vector3 Velocity => new Vector3(velocity.x, velocity.y, -velocity.z);
            public Vector3 AngularVelocity => new Vector3(-angularVelocity.x, -angularVelocity.y, angularVelocity.z);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Status
        {
            public ulong completed, skipped;
            public ulong completedSequence, unityGpuNs, endFrameNs;
            public uint active, focused, width, height;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlay
        {
            public Color foreground, background;
            public float grid;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern uint nhxr_frame_size();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern uint nhxr_hand_size();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr nhxr_last_error();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr nhxr_system_name();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int nhxr_open([MarshalAs(UnmanagedType.LPUTF8Str)] string loader, uint width, uint height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_begin(out Frame frame);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_queue(ulong frame, IntPtr left, IntPtr right, in Overlay overlay);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr nhxr_render_event();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_cancel();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_status(out Status status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_hand(uint hand, out Hand value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_stage_bounds(out float width, out float depth);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nhxr_haptic(uint hand, float amplitude, float frequency, float seconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int nhxr_close();

        public static string SystemName { get; private set; } = string.Empty;

        internal static void Open(string loader, uint width, uint height)
        {
            if (Marshal.SizeOf<Frame>() != nhxr_frame_size() || Marshal.SizeOf<Hand>() != nhxr_hand_size())
                throw new InvalidOperationException("The Unity and Metal OpenXR interop layouts differ.");
            Check(nhxr_open(loader, width, height));
            // The system name is fixed for the lifetime of the OpenXR session.
            SystemName = Marshal.PtrToStringUTF8(nhxr_system_name());
        }

        internal static void Close()
        {
            nhxr_close();
            SystemName = string.Empty;
        }

        internal static void Check(int result)
        {
            if (result < 0)
                throw new InvalidOperationException("OpenXR " + Marshal.PtrToStringUTF8(nhxr_last_error()));
        }

        public static bool TryHand(uint index, out Hand hand)
        {
            return nhxr_hand(index, out hand) == 0 && (hand.flags & 1) != 0;
        }

        public static bool Haptic(uint index, float amplitude, float frequency, float seconds)
        {
            return nhxr_haptic(index, amplitude, frequency, seconds) == 0;
        }
    }
}
