using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace NativeH3VR
{
    public static class NativeSteamVRSystem
    {
        public static bool Running;
        public static ETrackingUniverseOrigin TrackingSpace =
            ETrackingUniverseOrigin.TrackingUniverseStanding;
        static readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>(2);
        static readonly List<Vector3> boundary = new List<Vector3>(32);

        static uint Role(uint index) { return index == 0 ? 1u : index == 1 ? 2u : index == 2 ? 3u : 0u; }

        static HmdMatrix34_t Matrix(XRBridge.Pose pose)
        {
            Matrix4x4 m = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
            return new HmdMatrix34_t
            {
                m0 = m.m00,
                m1 = m.m01,
                m2 = -m.m02,
                m3 = m.m03,
                m4 = m.m10,
                m5 = m.m11,
                m6 = -m.m12,
                m7 = m.m13,
                m8 = -m.m20,
                m9 = -m.m21,
                m10 = m.m22,
                m11 = -m.m23
            };
        }

        static bool TryBoundary()
        {
            inputSubsystems.Clear();
            SubsystemManager.GetInstances(inputSubsystems);
            for (int i = 0; i < inputSubsystems.Count; i++)
                if (inputSubsystems[i].running && inputSubsystems[i].TryGetBoundaryPoints(boundary) &&
                    boundary.Count >= 3)
                    return true;
            boundary.Clear();
            return false;
        }

        public static void ResetSeatedZeroPose()
        {
            inputSubsystems.Clear();
            SubsystemManager.GetInstances(inputSubsystems);
            for (int i = 0; i < inputSubsystems.Count; i++)
                if (inputSubsystems[i].running && inputSubsystems[i].TryRecenter())
                    return;
            throw new NotSupportedException("The active Unity XR provider cannot recenter the tracking origin.");
        }

        // No skybox override can be installed because Unity XR owns scene
        // rendering. Clearing a nonexistent compositor override is idempotent.
        public static void ClearSkyboxOverride() { }

        public static void GetRecommendedRenderTargetSize(ref uint width, ref uint height)
        {
            if (!XRBridge.IsRunning)
                throw new InvalidOperationException("No active Unity XR display subsystem.");
            width = (uint)Mathf.Max(1, XRSettings.eyeTextureWidth);
            height = (uint)Mathf.Max(1, XRSettings.eyeTextureHeight);
        }

        static Camera ActiveCamera()
        {
            Camera camera = Camera.main;
            if (camera == null || !XRBridge.IsRunning)
                throw new InvalidOperationException("Unity XR has no active stereo camera.");
            return camera;
        }

        static Camera.StereoscopicEye StereoEye(EVREye eye)
        {
            return eye == EVREye.Eye_Left ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
        }

        public static HmdMatrix44_t GetProjectionMatrix(EVREye eye, float near, float far)
        {
            Matrix4x4 m = ActiveCamera().GetStereoProjectionMatrix(StereoEye(eye));
            return new HmdMatrix44_t
            {
                m0 = m.m00,
                m1 = m.m01,
                m2 = m.m02,
                m3 = m.m03,
                m4 = m.m10,
                m5 = m.m11,
                m6 = m.m12,
                m7 = m.m13,
                m8 = m.m20,
                m9 = m.m21,
                m10 = m.m22,
                m11 = m.m23,
                m12 = m.m30,
                m13 = m.m31,
                m14 = m.m32,
                m15 = m.m33
            };
        }

        public static void GetProjectionRaw(EVREye eye, ref float left, ref float right,
                                             ref float top, ref float bottom)
        {
            Matrix4x4 p = ActiveCamera().GetStereoProjectionMatrix(StereoEye(eye));
            if (Mathf.Abs(p.m00) < 1e-6f || Mathf.Abs(p.m11) < 1e-6f)
                throw new InvalidOperationException("Unity XR returned a degenerate eye projection.");
            left = (p.m02 - 1f) / p.m00;
            right = (p.m02 + 1f) / p.m00;
            top = (p.m12 + 1f) / p.m11;
            bottom = (p.m12 - 1f) / p.m11;
        }

        public static HmdMatrix34_t GetEyeToHeadTransform(EVREye eye)
        {
            Camera camera = ActiveCamera();
            Matrix4x4 eyeToHead = camera.transform.worldToLocalMatrix *
                                  camera.GetStereoViewMatrix(StereoEye(eye)).inverse;
            return new HmdMatrix34_t
            {
                m0 = eyeToHead.m00,
                m1 = eyeToHead.m01,
                m2 = -eyeToHead.m02,
                m3 = eyeToHead.m03,
                m4 = eyeToHead.m10,
                m5 = eyeToHead.m11,
                m6 = -eyeToHead.m12,
                m7 = eyeToHead.m13,
                m8 = -eyeToHead.m20,
                m9 = -eyeToHead.m21,
                m10 = eyeToHead.m22,
                m11 = -eyeToHead.m23
            };
        }

        public static bool IsTrackedDeviceConnected(uint device)
        {
            NativeSteamVRInput.RefreshTracking();
            return (NativeSteamVRInput.Snapshot.Role(Role(device)).flags & NativeTracking.Connected) != 0;
        }
        public static ETrackedDeviceClass GetTrackedDeviceClass(uint device)
        {
            if (!IsTrackedDeviceConnected(device)) return ETrackedDeviceClass.Invalid;
            return device == 0 ? ETrackedDeviceClass.HMD : ETrackedDeviceClass.Controller;
        }
        public static uint GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole role)
        {
            return role == ETrackedControllerRole.LeftHand ? 1u :
                   role == ETrackedControllerRole.RightHand ? 2u : uint.MaxValue;
        }
        public static ETrackedControllerRole GetControllerRoleForTrackedDeviceIndex(uint device)
        {
            return device == 1 ? ETrackedControllerRole.LeftHand :
                   device == 2 ? ETrackedControllerRole.RightHand : ETrackedControllerRole.Invalid;
        }

        public static void GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin origin,
                                                            float prediction,
                                                            TrackedDevicePose_t[] poses)
        {
            if (poses == null) throw new ArgumentNullException("poses");
            if (prediction != 0 || origin == ETrackingUniverseOrigin.TrackingUniverseSeated)
                throw new NotSupportedException("Unity XR does not expose predicted or seated poses through this adapter.");
            NativeSteamVRInput.RefreshTracking();
            Array.Clear(poses, 0, poses.Length);
            for (uint i = 0; i < poses.Length && i < 3; i++)
                poses[i] = NativeSteamVRInput.Pose(NativeSteamVRInput.Snapshot.Role(Role(i)), origin);
        }

        public static EVRCompositorError GetLastPoses(TrackedDevicePose_t[] render,
                                                       TrackedDevicePose_t[] game)
        {
            if (!Running || !XRBridge.IsRunning) return EVRCompositorError.RequestFailed;
            if (render != null) GetDeviceToAbsoluteTrackingPose(TrackingSpace, 0, render);
            if (game != null) GetDeviceToAbsoluteTrackingPose(TrackingSpace, 0, game);
            return EVRCompositorError.None;
        }

        public static ChaperoneCalibrationState GetCalibrationState()
        {
            return TryBoundary() ? ChaperoneCalibrationState.OK
                                 : ChaperoneCalibrationState.Error_PlayAreaInvalid;
        }
        public static bool GetPlayAreaSize(ref float width, ref float depth)
        {
            if (!TryBoundary()) return false;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            for (int i = 0; i < boundary.Count; i++)
            {
                minX = Mathf.Min(minX, boundary[i].x); maxX = Mathf.Max(maxX, boundary[i].x);
                minZ = Mathf.Min(minZ, boundary[i].z); maxZ = Mathf.Max(maxZ, boundary[i].z);
            }
            width = maxX - minX; depth = maxZ - minZ;
            return width > 0 && depth > 0;
        }
        public static bool GetPlayAreaRect(ref HmdQuad_t rect)
        {
            rect = default(HmdQuad_t);
            float width = 0, depth = 0;
            if (!GetPlayAreaSize(ref width, ref depth)) return false;
            float x = width / 2, z = depth / 2;
            rect = new HmdQuad_t
            {
                vCorners0 = new HmdVector3_t { v0 = -x, v2 = -z },
                vCorners1 = new HmdVector3_t { v0 = x, v2 = -z },
                vCorners2 = new HmdVector3_t { v0 = x, v2 = z },
                vCorners3 = new HmdVector3_t { v0 = -x, v2 = z }
            };
            return true;
        }
        public static bool AreBoundsVisible() { return false; }
        public static bool SceneRenderingSuspended { get; private set; }
        public static bool CanRenderScene() { return Running && XRBridge.IsRunning && !SceneRenderingSuspended; }
        public static void SuspendRendering(bool suspend) { SceneRenderingSuspended = suspend; }
        public static void SetTrackingSpace(ETrackingUniverseOrigin space)
        {
            if (space != ETrackingUniverseOrigin.TrackingUniverseStanding &&
                space != ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated)
                throw new NotSupportedException("Only Unity XR's floor-relative tracking origin is exposed.");
            TrackingSpace = space;
        }
        public static ETrackingUniverseOrigin GetTrackingSpace() { return TrackingSpace; }
        public static bool PollNextEvent(ref VREvent_t data, uint size)
        {
            data = default(VREvent_t);
            return false;
        }
        public static bool IsInputAvailable() { return Running && NativeSteamVRInput.InputFocused; }
        public static bool IsSteamVRDrawingControllers() { return false; }
        public static bool ShouldApplicationPause() { return !XRBridge.IsRunning; }
        public static bool ShouldApplicationReduceRenderingWork() { return !XRBridge.IsRunning; }
        public static string GetPropErrorNameFromEnum(ETrackedPropertyError error) { return error.ToString(); }

        public static uint GetStringTrackedDeviceProperty(uint device, ETrackedDeviceProperty property,
                                                           StringBuilder value, uint size,
                                                           ref ETrackedPropertyError error)
        {
            error = ETrackedPropertyError.TrackedProp_Success;
            NativeTracking.Device tracked = NativeSteamVRInput.Snapshot.Role(Role(device));
            string text;
            switch (property)
            {
                case ETrackedDeviceProperty.Prop_TrackingSystemName_String: text = "Unity OpenXR"; break;
                case ETrackedDeviceProperty.Prop_ModelNumber_String:
                    text = device == 0 ? XRSettings.loadedDeviceName : tracked.name; break;
                case ETrackedDeviceProperty.Prop_ControllerType_String:
                    text = device == 0 ? "hmd" : "openxr-controller"; break;
                case ETrackedDeviceProperty.Prop_SerialNumber_String: text = tracked.serial; break;
                case ETrackedDeviceProperty.Prop_ManufacturerName_String: text = "OpenXR runtime"; break;
                default: error = ETrackedPropertyError.TrackedProp_UnknownProperty; return 0;
            }
            if (string.IsNullOrEmpty(text)) { error = ETrackedPropertyError.TrackedProp_NotYetAvailable; return 0; }
            uint required = (uint)Encoding.UTF8.GetByteCount(text) + 1;
            if (value == null || size < required)
            {
                if (value != null) error = ETrackedPropertyError.TrackedProp_BufferTooSmall;
                return required;
            }
            value.Length = 0; value.Append(text); return required;
        }

        public static float GetFloatTrackedDeviceProperty(uint device, ETrackedDeviceProperty property,
                                                           ref ETrackedPropertyError error)
        {
            error = ETrackedPropertyError.TrackedProp_Success;
            if (property == ETrackedDeviceProperty.Prop_UserIpdMeters_Float)
            {
                Camera camera = Camera.main;
                if (camera == null) { error = ETrackedPropertyError.TrackedProp_NotYetAvailable; return 0; }
                return Vector3.Distance(camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3),
                                        camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3));
            }
            error = ETrackedPropertyError.TrackedProp_UnknownProperty;
            return 0;
        }

        public static void FadeToColor(float seconds, float r, float g, float b, float a, bool background)
        {
            throw new NotSupportedException("The Unity XR provider does not expose compositor fade controls.");
        }
        public static void FadeGrid(float seconds, bool fadeIn)
        {
            throw new NotSupportedException("The Unity XR provider does not expose compositor grid controls.");
        }
        public static HmdColor_t GetCurrentFadeColor(bool background)
        {
            throw new NotSupportedException("The Unity XR provider does not expose compositor fade state.");
        }
        public static float GetCurrentGridAlpha()
        {
            throw new NotSupportedException("The Unity XR provider does not expose compositor grid state.");
        }
    }
}
