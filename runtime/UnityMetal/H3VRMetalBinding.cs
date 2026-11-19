using System;
using UnityEngine;

namespace NativeH3VR
{
    // The importer owns SteamVR-shaped input. The native client only knows OpenXR.
    internal sealed class H3VRMetalBinding : IMetalOpenXRPlayer
    {
        readonly NativeStereoRenderer renderer;
        H3VRMetalBinding(NativeStereoRenderer renderer) { this.renderer = renderer; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            if (Application.platform != RuntimePlatform.OSXPlayer) return;
            try
            {
                XRBridge.Register(new H3VRMetalBinding(NativeStereoRenderer.Install()));
                Debug.Log("NATIVEH3VR_METAL_HOST_READY");
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Application.Quit(1);
            }
        }

        public bool Running => renderer.Running;
        public bool Focused => renderer.Focused;
        public uint EyeWidth => renderer.EyeWidth;
        public uint EyeHeight => renderer.EyeHeight;
        public string SystemName => MetalOpenXR.SystemName;

        public void BindCamera(Camera camera, Transform origin) => renderer.BindCamera(camera, origin);
        public void Suspend(bool suspend) => renderer.SuspendSceneRendering = suspend;
        public bool IsEyeCamera(Camera camera) => renderer.IsRenderingEye && camera == renderer.SourceCamera;
        public void FadeTo(Color color, float seconds, bool background) => renderer.FadeTo(color, seconds, background);
        public Color FadeColor(bool background) => renderer.FadeColor(background);
        public void FadeGrid(float seconds, bool fadeIn) => renderer.FadeGrid(seconds, fadeIn);
        public float GridAlpha => renderer.GridAlpha;

        public void RestoreProjection(Camera camera)
        {
            if (IsEyeCamera(camera)) camera.projectionMatrix = renderer.EyeProjection;
            else camera.ResetProjectionMatrix();
        }

        MetalOpenXR.View Eye(int eye) => eye == 0 ? renderer.LatestFrame.left : renderer.LatestFrame.right;
        public Vector4 Fov(int eye)
        {
            var view = Eye(eye);
            return new Vector4(view.left, view.right, view.up, view.down);
        }

        public bool StageBounds(out float width, out float depth) => MetalOpenXR.nhxr_stage_bounds(out width, out depth) == 0;

        static XRBridge.Pose Pose(MetalOpenXR.Pose value)
        {
            return new XRBridge.Pose { position = value.Position, rotation = value.Rotation };
        }

        public XRBridge.Pose EyeOffset(int eye)
        {
            var head = renderer.LatestFrame.head;
            var eyePose = Eye(eye).pose;
            Quaternion inverse = Quaternion.Inverse(head.Rotation);
            return new XRBridge.Pose
            {
                position = inverse * (eyePose.Position - head.Position),
                rotation = inverse * eyePose.Rotation
            };
        }

        public int Read(out NativeTracking.Snapshot snapshot)
        {
            snapshot = default;
            if (renderer.Sequence == 0) return -1;
            var head = Pose(renderer.LatestFrame.head);
            var headAxes = new float[16];
            // Core OpenXR exposes session focus but not a headset wear sensor.
            headAxes[1] = Focused ? 1 : 0;
            var devices = new NativeTracking.Device[3];
            devices[0] = new NativeTracking.Device
            {
                role = 1,
                name = SystemName,
                flags = NativeTracking.Connected | NativeTracking.PoseValid |
                        (Focused ? NativeTracking.InputCurrent : 0),
                trackingPose = head,
                gripPose = head,
                aimPose = head,
                axes = headAxes
            };
            for (uint i = 0; i < 2; i++)
                devices[i + 1] = SyntheticHands.TryHand(i + 2, out var sample) ? sample : ReadHand(i, i + 2);
            snapshot = new NativeTracking.Snapshot { devices = devices, deviceCount = 3 };
            return 0;
        }

        static NativeTracking.Device ReadHand(uint index, uint role)
        {
            if (!MetalOpenXR.TryHand(index, out var hand)) return new NativeTracking.Device { role = role };
            var axes = new float[16];
            axes[1] = hand.trigger;
            axes[2] = hand.trackpadX;
            axes[3] = hand.trackpadY;
            axes[4] = axes[5] = axes[6] = hand.squeeze;
            axes[7] = hand.trigger;
            axes[8] = hand.squeezeForce;
            axes[9] = hand.trackpadForce;
            axes[10] = hand.thumbstickX;
            axes[11] = hand.thumbstickY;
            return new NativeTracking.Device
            {
                role = role,
                name = hand.profile == MetalOpenXR.ControllerProfile.ValveIndex ? "Valve Index Controller" : "OpenXR controller",
                flags = hand.flags,
                buttons = hand.buttons,
                touches = hand.touches | (hand.squeeze > .05f ? 128u : 0u),
                trackingPose = Pose(hand.grip),
                gripPose = Pose(hand.grip),
                aimPose = Pose(hand.aim),
                linearVelocity = hand.Velocity,
                angularVelocity = hand.AngularVelocity,
                axes = axes
            };
        }

        public bool Haptic(uint role, float amplitude, float frequency, float duration)
        {
            return (role == 2 || role == 3) &&
                   MetalOpenXR.Haptic(role - 2, Mathf.Clamp01(amplitude), frequency, Mathf.Clamp(duration, 0, 10));
        }
    }
}
