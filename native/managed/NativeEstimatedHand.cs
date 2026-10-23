using System;
using UnityEngine;
using Valve.VR;
namespace NativeH3VR {
// An explicit estimate, not Valve's hand solver or measured joint tracking.
// Pinned libsurvive axes: 4/5/6 middle/ring/pinky proximity, 7 index
// proximity, 1 trigger travel, 8 grip force. Proximity is normalized 0..1.
public static class NativeEstimatedHand {
    static float Unit(float value) {
        return float.IsNaN(value) || float.IsInfinity(value) ? 0 : Mathf.Clamp01(value);
    }
    public static VRSkeletalSummaryData_t Summary(NativeTracking.Device device) {
        float force = Unit(device.Axis(8));
        // Force supplies a lower bound for fingers pressing the controller.
        // Proximity preserves a curled hold after squeeze force is released.
        float middle = Mathf.Max(Unit(device.Axis(4)), force);
        float ring = Mathf.Max(Unit(device.Axis(5)), force);
        float pinky = Mathf.Max(Unit(device.Axis(6)), force);
        float index = Mathf.Max(Unit(device.Axis(7)), Unit(device.Axis(1)));
        if ((device.touches & 1u) != 0)
            index = Mathf.Max(index, .35f);
        // No thumb-position sensor: resting on a thumb control is an
        // explicit contact-based estimate, otherwise assume extended.
        float thumb = (device.touches & (2u | 4u | 16u | 32u)) != 0 ? .7f : 0f;
        return new VRSkeletalSummaryData_t {
            flFingerCurl0 = thumb, flFingerCurl1 = index, flFingerCurl2 = middle,
            flFingerCurl3 = ring, flFingerCurl4 = pinky,
            // Splay is not measured. Neutral estimated splay, never Full.
            flFingerSplay0 = 0, flFingerSplay1 = 0, flFingerSplay2 = 0, flFingerSplay3 = 0
        };
    }
}
}
