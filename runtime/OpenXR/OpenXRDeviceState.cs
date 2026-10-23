using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace NativeH3VR
{
    // Compatibility-shaped tracking records used by the original SteamVR action
    // facade. All data comes from Unity's selected XR provider (normally OpenXR).
    public static class NativeTracking
    {
        public const uint Connected = 1, PoseValid = 2, PoseTracked = 4, VelocityValid = 8,
                          InputCurrent = 16, ControllerOffsets = 64;

        [Serializable]
        public struct Device
        {
            public uint role, flags;
            public string name, serial;
            public XRBridge.Pose trackingPose, gripPose, aimPose;
            public Vector3 linearVelocity, angularVelocity;
            public double poseAgeSeconds, opticalAgeSeconds;
            public uint buttons, touches;
            public float[] axes;
            public float Axis(int index)
            {
                return axes != null && index >= 0 && index < axes.Length ? axes[index] : 0f;
            }
        }

        public struct Snapshot
        {
            public uint deviceCount, floorState;
            public double floorY;
            public Device[] devices;
            public Device Role(uint role)
            {
                if (devices != null)
                    for (int i = 0; i < deviceCount && i < devices.Length; i++)
                        if (devices[i].role == role)
                            return devices[i];
                return new Device { role = role };
            }
        }

        static readonly List<InputDevice> leftDevices = new List<InputDevice>(4);
        static readonly List<InputDevice> rightDevices = new List<InputDevice>(4);

        static InputDevice GetDevice(XRNode node, List<InputDevice> devices)
        {
            devices.Clear();
            InputDevices.GetDevicesAtXRNode(node, devices);
            for (int i = 0; i < devices.Count; i++)
                if (devices[i].isValid)
                    return devices[i];
            return default(InputDevice);
        }

        static bool ReadDevice(InputDevice input, uint role, out Device output)
        {
            output = new Device { role = role, axes = new float[16] };
            if (!input.isValid)
                return false;

            output.flags = Connected;
            bool havePosition = input.TryGetFeatureValue(CommonUsages.devicePosition,
                                                          out output.trackingPose.position);
            bool haveRotation = input.TryGetFeatureValue(CommonUsages.deviceRotation,
                                                          out output.trackingPose.rotation);
            bool isTracked = input.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked);
            bool haveTrackingState = input.TryGetFeatureValue(CommonUsages.trackingState,
                                                                out InputTrackingState trackingState);
            bool tracksPose = haveTrackingState &&
                              (trackingState & (InputTrackingState.Position | InputTrackingState.Rotation)) ==
                              (InputTrackingState.Position | InputTrackingState.Rotation);
            if (havePosition && haveRotation && ((isTracked && tracked) || tracksPose))
                output.flags |= PoseValid | PoseTracked;
            output.gripPose = output.aimPose = output.trackingPose;
            if (input.TryGetFeatureValue(CommonUsages.deviceVelocity, out output.linearVelocity) |
                input.TryGetFeatureValue(CommonUsages.deviceAngularVelocity,
                                         out output.angularVelocity))
                output.flags |= VelocityValid;

            float trigger = 0, grip = 0;
            bool triggerButton = false, gripButton = false, stickClick = false;
            bool primary = false, secondary = false, menu = false, presence = false;
            bool stickTouch = false, primaryTouch = false, secondaryTouch = false;
            Vector2 stick = Vector2.zero;
            Vector2 secondaryStick = Vector2.zero;
            input.TryGetFeatureValue(CommonUsages.trigger, out trigger);
            input.TryGetFeatureValue(CommonUsages.grip, out grip);
            input.TryGetFeatureValue(CommonUsages.triggerButton, out triggerButton);
            input.TryGetFeatureValue(CommonUsages.gripButton, out gripButton);
            input.TryGetFeatureValue(CommonUsages.primary2DAxis, out stick);
            input.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out stickClick);
            input.TryGetFeatureValue(CommonUsages.primary2DAxisTouch, out stickTouch);
            input.TryGetFeatureValue(CommonUsages.secondary2DAxis, out secondaryStick);
            input.TryGetFeatureValue(CommonUsages.primaryTouch, out primaryTouch);
            input.TryGetFeatureValue(CommonUsages.secondaryTouch, out secondaryTouch);
            input.TryGetFeatureValue(CommonUsages.primaryButton, out primary);
            input.TryGetFeatureValue(CommonUsages.secondaryButton, out secondary);
            input.TryGetFeatureValue(CommonUsages.menuButton, out menu);
            input.TryGetFeatureValue(CommonUsages.userPresence, out presence);

            output.axes[1] = Mathf.Clamp01(trigger);
            output.axes[2] = stick.x;
            output.axes[3] = stick.y;
            output.axes[10] = secondaryStick.x;
            output.axes[11] = secondaryStick.y;
            output.axes[8] = Mathf.Clamp01(grip);
            output.axes[9] = 0f; // Unity XR has no portable thumb-squeeze feature.
            if (triggerButton || trigger >= .95f) output.buttons |= 1;
            if (stickClick) output.buttons |= 2;
            if (primary) output.buttons |= 16;
            if (secondary) output.buttons |= 32;
            if (gripButton || grip >= .95f) output.buttons |= 128;
            if (menu) output.buttons |= 256;
            if (trigger > .05f) output.touches |= 1;
            if (stickTouch || stick.sqrMagnitude > .0025f) output.touches |= 2;
            if (secondaryTouch) output.touches |= 4;
            if (primaryTouch) output.touches |= 16;
            if (secondaryTouch) output.touches |= 32;
            if (role == 1 && presence) output.axes[1] = 1f;
            if ((input.characteristics & InputDeviceCharacteristics.Controller) != 0)
                output.flags |= InputCurrent;
            output.name = input.name;
            output.serial = input.serialNumber;
            return true;
        }

        public static int Read(out Snapshot snapshot)
        {
            snapshot = new Snapshot { devices = new Device[3], deviceCount = 3 };
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!ReadDevice(head, 1, out snapshot.devices[0]))
                return -1;
            ReadDevice(GetDevice(XRNode.LeftHand, leftDevices), 2, out snapshot.devices[1]);
            ReadDevice(GetDevice(XRNode.RightHand, rightDevices), 3, out snapshot.devices[2]);
            return 0;
        }

        public static InputDevice GetController(uint role)
        {
            return role == 2 ? GetDevice(XRNode.LeftHand, leftDevices)
                 : role == 3 ? GetDevice(XRNode.RightHand, rightDevices)
                             : default(InputDevice);
        }
    }

    public static class XRBridge
    {
        public delegate bool OpenXRHapticSender(InputDevice device, float amplitude, float frequency,
                                                float duration);
        static OpenXRHapticSender openXRHapticSender;

        public struct Pose
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        public static bool IsRunning
        {
            get { return XRSettings.enabled && XRSettings.isDeviceActive; }
        }

        public static bool InputFocused { get { return IsRunning && OpenXRSessionState.InputFocus; } }

        public static int Haptic(uint role, float amplitude, float frequency, float duration)
        {
            InputDevice device = NativeTracking.GetController(role);
            if (!device.isValid || !device.TryGetHapticCapabilities(out HapticCapabilities caps) ||
                !caps.supportsImpulse)
                return -1;
            try
            {
                amplitude = Mathf.Clamp01(amplitude);
                duration = Mathf.Clamp(duration, 0, 10);
                if (openXRHapticSender != null)
                    return openXRHapticSender(device, amplitude, frequency, duration) ? 0 : -1;
                // Generic Unity XR providers expose no frequency parameter.
                // Keep their amplitude/duration path only for the default frequency.
                if (frequency != 0)
                    return -1;
                return device.SendHapticImpulse(0, amplitude, duration) ? 0 : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static void SetOpenXRHapticSender(OpenXRHapticSender sender)
        {
            openXRHapticSender = sender;
        }
    }

    // Fail closed because Unity's cross-provider XR API reports device validity,
    // not whether the OpenXR session currently owns input focus. A provider
    // integration must report XR_SESSION_STATE_FOCUSED transitions here.
    public static class OpenXRSessionState
    {
        static bool inputFocus;
        public static bool InputFocus { get { return inputFocus; } }
        public static void SetInputFocus(bool focused) { inputFocus = focused; }
    }
}
