using NativeH3VR;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Input;
using UnityEngine.XR.OpenXR.Features;

#if UNITY_EDITOR
using UnityEditor.XR.OpenXR.Features;
[OpenXRFeature(
    UiName = "H3VR OpenXR session focus bridge",
    Desc = "Reports standard OpenXR session focus to H3VR managed input gating.",
    Company = "NativeH3VR",
    Version = "1.0.0",
    FeatureId = "com.nativeh3vr.openxr.session-focus",
    BuildTargetGroups = new[] { UnityEditor.BuildTargetGroup.Standalone })]
#endif
public sealed class OpenXRSessionFeature : OpenXRFeature
{
    protected override bool OnInstanceCreate(ulong xrInstance)
    {
        OpenXRSessionState.SetInputFocus(false);
        XRBridge.SetOpenXRHapticSender(SendHapticImpulse);
        return base.OnInstanceCreate(xrInstance);
    }

    protected override void OnInstanceDestroy(ulong xrInstance)
    {
        XRBridge.SetOpenXRHapticSender(null);
        OpenXRSessionState.SetInputFocus(false);
        base.OnInstanceDestroy(xrInstance);
    }

    protected override void OnSessionStateChange(int oldState, int newState)
    {
        // XR_SESSION_STATE_FOCUSED is value 5 in the OpenXR core specification.
        OpenXRSessionState.SetInputFocus(newState == 5);
    }

    static bool SendHapticImpulse(InputDevice device, float amplitude, float frequency, float duration)
    {
        if (!device.isValid)
            return false;
        OpenXRInput.SendHapticImpulse(device, amplitude, frequency, duration);
        return true;
    }
}
