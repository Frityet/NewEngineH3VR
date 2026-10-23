using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Valve.VR;
namespace NativeH3VR {
public static class NativeSteamVRSystem {
    public static uint Width = 1440, Height = 1600;
    public static XRBridge.View LeftEye, RightEye;
    public static bool Running;
    public static ETrackingUniverseOrigin TrackingSpace =
        ETrackingUniverseOrigin.TrackingUniverseStanding;
    public static void GetRecommendedRenderTargetSize(ref uint width, ref uint height) {
        width = Width;
        height = Height;
    }
    public static void GetProjectionRaw(EVREye eye, ref float left, ref float right, ref float top,
                                        ref float bottom) {
        var v = eye == EVREye.Eye_Left ? LeftEye : RightEye;
        left = Mathf.Tan(v.fovLeft);
        right = Mathf.Tan(v.fovRight);
        top = -Mathf.Tan(v.fovUp);
        bottom = -Mathf.Tan(v.fovDown);
    }
    public static HmdMatrix34_t Matrix(XRBridge.Pose pose) {
        Matrix4x4 m = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
        return new HmdMatrix34_t { m0 = m.m00,  m1 = m.m01,  m2 = -m.m02, m3 = m.m03,
                                   m4 = m.m10,  m5 = m.m11,  m6 = -m.m12, m7 = m.m13,
                                   m8 = -m.m20, m9 = -m.m21, m10 = m.m22, m11 = -m.m23 };
    }
    public static HmdMatrix34_t GetEyeToHeadTransform(EVREye eye) {
        XRBridge.View left, right;
        if (Running && XRBridge.nh3vr_xr_eye_views(out left, out right) == 0) {
            LeftEye = left;
            RightEye = right;
        }
        return Matrix((eye == EVREye.Eye_Left ? LeftEye : RightEye).pose);
    }
    static uint Role(uint device) {
        return device == 0 ? 1u : device == 1 ? 2u : device == 2 ? 3u : 0u;
    }
    public static bool IsTrackedDeviceConnected(uint device) {
        if (device == 0) {
            XRBridge.Pose pose;
            return Running && XRBridge.nh3vr_xr_head_pose(out pose) == 0;
        }
        return (NativeSteamVRInput.Snapshot.Role(Role(device)).flags & NativeTracking.Connected) !=
               0;
    }
    public static ETrackedDeviceClass GetTrackedDeviceClass(uint device) {
        return !IsTrackedDeviceConnected(device) ? ETrackedDeviceClass.Invalid
               : device == 0                     ? ETrackedDeviceClass.HMD
                                                 : ETrackedDeviceClass.Controller;
    }
    public static uint GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole role) {
        return role == ETrackedControllerRole.LeftHand    ? 1u
               : role == ETrackedControllerRole.RightHand ? 2u
                                                          : uint.MaxValue;
    }
    public static ETrackedControllerRole GetControllerRoleForTrackedDeviceIndex(uint device) {
        return device == 1   ? ETrackedControllerRole.LeftHand
               : device == 2 ? ETrackedControllerRole.RightHand
                             : ETrackedControllerRole.Invalid;
    }
    public static void GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin origin,
                                                       float prediction,
                                                       TrackedDevicePose_t[] poses) {
        if (origin == ETrackingUniverseOrigin.TrackingUniverseSeated || prediction != 0)
            throw new NotSupportedException(
                "Native tracking does not provide seated recenter or prediction.");
        Array.Clear(poses, 0, poses.Length);
        for (uint i = 0; i < poses.Length && i < 3; i++)
            poses[i] = NativeSteamVRInput.Pose(NativeSteamVRInput.Snapshot.Role(Role(i)), origin);
        if (poses.Length > 0) {
            XRBridge.Pose head;
            if (Running && XRBridge.nh3vr_xr_head_pose(out head) == 0) {
                if (origin == ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated &&
                    NativeSteamVRInput.Snapshot.floorState == 2)
                    head.position.y += (float)NativeSteamVRInput.Snapshot.floorY;
                poses[0] = new TrackedDevicePose_t { bDeviceIsConnected = true, bPoseIsValid = true,
                                                     eTrackingResult = ETrackingResult.Running_OK,
                                                     mDeviceToAbsoluteTracking = Matrix(head) };
            }
        }
    }
    public static EVRCompositorError GetLastPoses(TrackedDevicePose_t[] render,
                                                  TrackedDevicePose_t[] game) {
        if (!Running)
            return EVRCompositorError.RequestFailed;
        NativeSteamVRInput.RefreshTracking();
        if (render != null)
            GetDeviceToAbsoluteTrackingPose(TrackingSpace, 0, render);
        if (game != null)
            GetDeviceToAbsoluteTrackingPose(TrackingSpace, 0, game);
        return EVRCompositorError.None;
    }
    public static ChaperoneCalibrationState GetCalibrationState() {
        float width, depth;
        return XRBridge.nh3vr_xr_stage_bounds(out width, out depth) == 0
                   ? ChaperoneCalibrationState.OK
                   : ChaperoneCalibrationState.Error_PlayAreaInvalid;
    }
    public static bool GetPlayAreaSize(ref float width, ref float depth) {
        return XRBridge.nh3vr_xr_stage_bounds(out width, out depth) == 0;
    }
    public static bool GetPlayAreaRect(ref HmdQuad_t rect) {
        rect = default(HmdQuad_t);
        float width, depth;
        if (XRBridge.nh3vr_xr_stage_bounds(out width, out depth) != 0)
            return false;
        float x = width / 2, z = depth / 2;
        rect = new HmdQuad_t { vCorners0 = new HmdVector3_t { v0 = -x, v2 = -z },
                               vCorners1 = new HmdVector3_t { v0 = x, v2 = -z },
                               vCorners2 = new HmdVector3_t { v0 = x, v2 = z },
                               vCorners3 = new HmdVector3_t { v0 = -x, v2 = z } };
        return true;
    }
    public static bool AreBoundsVisible() { return false; }
    public static bool SceneRenderingSuspended { get; private set; }
    public static bool CanRenderScene() { return Running && !SceneRenderingSuspended; }
    [DllImport("NativeH3VRXR")]
    static extern int nh3vr_xr_fade_color(float seconds, float r, float g, float b, float a,
                                          uint background);
    [DllImport("NativeH3VRXR")]
    static extern int nh3vr_xr_fade_grid(float seconds, uint fadeIn);
    [DllImport("NativeH3VRXR")]
    static extern int nh3vr_xr_fade_state(uint background, out float r, out float g, out float b,
                                          out float a, out float grid);
    public static void FadeToColor(float seconds, float r, float g, float b, float a,
                                   bool background) {
        XRBridge.Check(nh3vr_xr_fade_color(seconds, r, g, b, a, background ? 1u : 0u));
    }
    public static void FadeGrid(float seconds, bool fadeIn) {
        XRBridge.Check(nh3vr_xr_fade_grid(seconds, fadeIn ? 1u : 0u));
    }
    public static HmdColor_t GetCurrentFadeColor(bool background) {
        float r, g, b, a, grid;
        XRBridge.Check(
            nh3vr_xr_fade_state(background ? 1u : 0u, out r, out g, out b, out a, out grid));
        return new HmdColor_t { r = r, g = g, b = b, a = a };
    }
    public static float GetCurrentGridAlpha() {
        float r, g, b, a, grid;
        XRBridge.Check(nh3vr_xr_fade_state(0, out r, out g, out b, out a, out grid));
        return grid;
    }
    public static void SuspendRendering(bool suspend) {
        SceneRenderingSuspended = suspend;
        NativeSteamVRBootstrap.SetSceneRenderingSuspended(suspend);
    }
    public static void SetTrackingSpace(ETrackingUniverseOrigin space) {
        if (space != ETrackingUniverseOrigin.TrackingUniverseStanding)
            throw new NotSupportedException("Native stereo uses calibrated STAGE tracking space.");
        TrackingSpace = space;
    }
    public static ETrackingUniverseOrigin GetTrackingSpace() { return TrackingSpace; }
    public static bool PollNextEvent(ref VREvent_t data, uint size) {
        data = default(VREvent_t);
        return false;
    }
    public static bool IsInputAvailable() { return Running && NativeSteamVRInput.InputFocused; }
    public static bool IsSteamVRDrawingControllers() { return false; }
    public static bool ShouldApplicationPause() { return !Running; }
    public static bool ShouldApplicationReduceRenderingWork() { return !Running; }
    public static string GetPropErrorNameFromEnum(ETrackedPropertyError error) {
        return error.ToString();
    }
    public static uint GetStringTrackedDeviceProperty(uint device, ETrackedDeviceProperty property,
                                                      StringBuilder value, uint size,
                                                      ref ETrackedPropertyError error) {
        error = ETrackedPropertyError.TrackedProp_Success;
        string text = null;
        var tracked = NativeSteamVRInput.Snapshot.Role(Role(device));
        switch (property) {
        case ETrackedDeviceProperty.Prop_TrackingSystemName_String:
            text = "NativeH3VR OpenXR";
            break;
        case ETrackedDeviceProperty.Prop_ModelNumber_String:
            text = device == 0 ? NativeSteamVRBootstrap.HeadsetModel : "Knuckles";
            break;
        case ETrackedDeviceProperty.Prop_ControllerType_String:
            text = device == 0 ? "indexhmd" : "knuckles";
            break;
        case ETrackedDeviceProperty.Prop_SerialNumber_String:
            text = tracked.serial;
            break;
        case ETrackedDeviceProperty.Prop_ManufacturerName_String:
            text = "Valve";
            break;
        default:
            error = ETrackedPropertyError.TrackedProp_UnknownProperty;
            return 0;
        }
        if (string.IsNullOrEmpty(text)) {
            error = ETrackedPropertyError.TrackedProp_NotYetAvailable;
            return 0;
        }
        uint required = (uint)Encoding.UTF8.GetByteCount(text) + 1;
        if (value == null || size < required) {
            if (value != null)
                error = ETrackedPropertyError.TrackedProp_BufferTooSmall;
            return required;
        }
        value.Length = 0;
        value.Append(text);
        return required;
    }
    public static float GetFloatTrackedDeviceProperty(uint device, ETrackedDeviceProperty property,
                                                      ref ETrackedPropertyError error) {
        error = ETrackedPropertyError.TrackedProp_Success;
        if (property == ETrackedDeviceProperty.Prop_DisplayFrequency_Float)
            return XRBridge.nh3vr_xr_display_frequency();
        if (property == ETrackedDeviceProperty.Prop_UserIpdMeters_Float)
            return Vector3.Distance(LeftEye.pose.position, RightEye.pose.position);
        error = ETrackedPropertyError.TrackedProp_UnknownProperty;
        return 0;
    }
}
}
