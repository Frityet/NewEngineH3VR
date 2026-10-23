using System;
using System.Collections;
using FistVR;
using NativeH3VR;
using UnityEngine;
using Valve.VR;

/// <summary>
/// Bounded movement acceptance through the original H3VR movement manager and
/// SteamVR actions. Input is supplied by the caller so this fixture never
/// changes or owns the diagnostic-input transport.
/// </summary>
public static class NativePlayerMovementFixture {
    const float InputTimeout = 5f;
    const float MovementTimeout = 4f;
    const float HeadPositionTolerance = .05f;
    const float HeadAngleTolerance = 5f;

    static readonly Vector3 LeftHome = new Vector3(-.25f, 1.25f, .45f);
    static readonly Vector3 RightHome = new Vector3(.25f, 1.25f, .45f);

    static void DriveNeutral(NativePlayerHandDriver drive) {
        drive(2, LeftHome, Quaternion.identity, 0f, 0f, 0, 0, Vector2.zero, Vector2.zero);
        drive(3, RightHome, Quaternion.identity, 0f, 0f, 0, 0, Vector2.zero, Vector2.zero);
    }

    static void DriveLeft(NativePlayerHandDriver drive, Vector2 primary, Vector2 secondary,
                          uint buttons = 0, uint touches = 0) {
        drive(2, LeftHome, Quaternion.identity, 0f, 0f, buttons, touches, primary, secondary);
        drive(3, RightHome, Quaternion.identity, 0f, 0f, 0, 0, Vector2.zero, Vector2.zero);
    }

    static void DriveRight(NativePlayerHandDriver drive, Vector2 primary, Vector2 secondary,
                           uint buttons = 0, uint touches = 0) {
        drive(2, LeftHome, Quaternion.identity, 0f, 0f, 0, 0, Vector2.zero, Vector2.zero);
        drive(3, RightHome, Quaternion.identity, 0f, 0f, buttons, touches, primary, secondary);
    }

    static void CheckHeadPose(NativeStereoRenderer renderer, FVRMovementManager movement) {
        XRBridge.Pose pose;
        int status = XRBridge.nh3vr_xr_head_pose(out pose);
        if (status != 0)
            throw new InvalidOperationException(
                "OpenXR head pose unavailable while checking native player head synchronization: " +
                status);

        Vector3 expectedPosition = renderer.trackingOrigin.TransformPoint(pose.position);
        Quaternion expectedRotation = renderer.trackingOrigin.rotation * pose.rotation;
        float positionError = Vector3.Distance(movement.Head.position, expectedPosition);
        float angleError = Quaternion.Angle(movement.Head.rotation, expectedRotation);
        if (positionError > HeadPositionTolerance || angleError > HeadAngleTolerance)
            throw new InvalidOperationException(
                "Original H3VR head transform is not following OpenXR outside eye rendering. " +
                "positionError=" + positionError.ToString("F3") +
                "m angleError=" + angleError.ToString("F1") + "deg");
    }

    /// <summary>
    /// Requires a focused synthetic OpenXR session, two valid Index hands, and
    /// the caller's per-frame world-pose input driver. Temporarily selects the
    /// original TwinStick mode and LeveledHead basis in memory, checks that the
    /// original hand action receives thumbstick axes, then verifies player
    /// translation and snap-turn through the game's normal Update path.
    /// The previous movement mode/options are restored in finally and are never
    /// saved to disk.
    /// </summary>
    public static IEnumerator Run(NativePlayerHandDriver drive, Action<string, string> mark) {
        if (drive == null)
            throw new ArgumentNullException("drive");
        if (mark == null)
            throw new ArgumentNullException("mark");
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") != "synthetic" ||
            Environment.GetEnvironmentVariable("NATIVEH3VR_DIAGNOSTIC_INPUT") != "1")
            throw new InvalidOperationException("Native player movement fixture requires " +
                                                "explicit synthetic OpenXR diagnostic input.");

        float deadline = Time.realtimeSinceStartup + 30f;
        NativeStereoRenderer renderer = null;
        FVRMovementManager movement = null;
        FVRViveHand left = null, right = null;
        while (Time.realtimeSinceStartup < deadline) {
            DriveNeutral(drive);
            renderer = UnityEngine.Object.FindObjectOfType<NativeStereoRenderer>();
            movement = UnityEngine.Object.FindObjectOfType<FVRMovementManager>();
            if (renderer != null && renderer.trackingOrigin != null &&
                renderer.sourceCamera != null && movement != null && movement.Head != null &&
                movement.Hands != null && movement.Hands.Length >= 2 &&
                GM.CurrentPlayerBody != null) {
                left = movement.Hands[0];
                right = movement.Hands[1];
                if (left != null && right != null && left.Pose[left.HandSource].poseIsValid &&
                    right.Pose[right.HandSource].poseIsValid && left.CMode == ControlMode.Index &&
                    right.CMode == ControlMode.Index && NativeSteamVRInput.InputFocused)
                    break;
            }
            yield return null;
        }
        if (renderer == null || movement == null || left == null || right == null ||
            !NativeSteamVRInput.InputFocused || left.CMode != ControlMode.Index ||
            right.CMode != ControlMode.Index)
            throw new TimeoutException("Timed out waiting for focused OpenXR, original movement " +
                                       "manager, and valid Index hands.");

        var options = GM.Options.MovementOptions;
        var oldMode = movement.Mode;
        var oldCurrentMode = options.CurrentMovementMode;
        var oldHandSetup = options.TwinStickLeftRightState;
        var oldMovementBasis = options.Touchpad_MovementMode;
        var oldSnapMode = options.TwinStickSnapturnState;
        var oldSnapIndex = options.SnapTurnMagnitudeIndex;
        var oldAxButtonMode = options.AXButtonSnapTurnState;
        var oldSprintMode = options.TwinStickSprintState;
        try {
            mark("movement-neutral", "waiting for current neutral Index samples");
            float neutralDeadline = Time.realtimeSinceStartup + InputTimeout;
            int consecutiveNeutralFrames = 0;
            while (Time.realtimeSinceStartup < neutralDeadline) {
                DriveNeutral(drive);
                yield return null;
                if (left.Input.Secondary2AxisInputAxes.magnitude < .05f &&
                    right.Input.Secondary2AxisInputAxes.magnitude < .05f &&
                    left.Pose[left.HandSource].poseIsValid &&
                    right.Pose[right.HandSource].poseIsValid)
                    consecutiveNeutralFrames++;
                else
                    consecutiveNeutralFrames = 0;
                if (consecutiveNeutralFrames >= 2)
                    break;
            }
            if (consecutiveNeutralFrames < 2)
                throw new TimeoutException(
                    "Index hands did not produce valid neutral thumbstick samples.");

            CheckHeadPose(renderer, movement);
            options.CurrentMovementMode = FVRMovementManager.MovementMode.TwinStick;
            options.TwinStickLeftRightState = MovementOptions.TwinStickLeftRightSetup.LeftStickMove;
            options.Touchpad_MovementMode = FVRMovementManager.TwoAxisMovementMode.LeveledHead;
            options.TwinStickSnapturnState = MovementOptions.TwinStickSnapturnMode.Enabled;
            options.SnapTurnMagnitudeIndex = 1; // Original 45-degree snap-turn option.
            options.AXButtonSnapTurnState = MovementOptions.AXButtonSnapTurnMode.Snapturn;
            options.TwinStickSprintState = MovementOptions.TwinStickSprintMode.LeftStickClick;
            movement.Mode = FVRMovementManager.MovementMode.TwinStick;
            movement.CleanupFlagsForModeSwitch();

            mark("movement-thumbstick-axes",
                 "left Index secondary2axis north; original TwinStick/LeveledHead");
            float axisDeadline = Time.realtimeSinceStartup + InputTimeout;
            while (Time.realtimeSinceStartup < axisDeadline &&
                   left.Input.Secondary2AxisInputAxes.y < .8f) {
                DriveLeft(drive, Vector2.zero, new Vector2(0f, 1f));
                yield return null;
            }
            if (left.Input.Secondary2AxisInputAxes.y < .8f)
                throw new TimeoutException("Native thumbstick axis did not reach original " +
                                           "FVRViveHand Secondary2Axis input.");

            CheckHeadPose(renderer, movement);
            Vector3 beforeMove = movement.transform.position;
            Vector3 expectedForward = GM.CurrentPlayerBody.Head.forward;
            expectedForward.y = 0f;
            expectedForward.Normalize();
            float moveDeadline = Time.realtimeSinceStartup + MovementTimeout;
            Vector3 afterMove = beforeMove;
            while (Time.realtimeSinceStartup < moveDeadline &&
                   Vector3.Distance(beforeMove, afterMove) < .12f) {
                DriveLeft(drive, Vector2.zero, new Vector2(0f, 1f));
                CheckHeadPose(renderer, movement);
                afterMove = movement.transform.position;
                yield return null;
            }
            Vector3 displacement = afterMove - beforeMove;
            displacement.y = 0f;
            if (displacement.magnitude < .12f)
                throw new TimeoutException(
                    "Original TwinStick input produced no measurable player movement.");
            if (expectedForward.sqrMagnitude < .5f ||
                Vector3.Dot(displacement.normalized, expectedForward) < .70f)
                throw new InvalidOperationException(
                    "TwinStick movement did not follow the original head-relative forward basis. " +
                    "dot=" + Vector3.Dot(displacement.normalized, expectedForward).ToString("F2"));
            mark("movement-thumbstick-pass",
                 "displacement=" + displacement.ToString("F3") + "m; head-relative dot=" +
                     Vector3.Dot(displacement.normalized, expectedForward).ToString("F2"));

            mark("movement-snap-turn", "right Index secondary2axis west edge; expected 45 degrees");
            float releaseDeadline = Time.realtimeSinceStartup + .75f;
            while (Time.realtimeSinceStartup < releaseDeadline) {
                DriveNeutral(drive);
                CheckHeadPose(renderer, movement);
                yield return null;
            }
            Vector3 forwardBeforeTurn = GM.CurrentPlayerBody.transform.forward;
            DriveRight(drive, Vector2.zero, Vector2.left);
            float edgeDeadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < edgeDeadline &&
                   left.Input.Secondary2AxisWestDown == false &&
                   right.Input.Secondary2AxisWestDown == false) {
                DriveRight(drive, Vector2.zero, Vector2.left);
                yield return null;
            }
            if (!right.Input.Secondary2AxisWestDown && !right.Input.Secondary2AxisWestPressed)
                throw new TimeoutException(
                    "Native thumbstick west edge did not reach original FVRViveHand input.");
            float turnDeadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < turnDeadline &&
                   Vector3.Angle(forwardBeforeTurn, GM.CurrentPlayerBody.transform.forward) < 30f) {
                DriveRight(drive, Vector2.zero, Vector2.left);
                yield return null;
            }
            float turned = Vector3.Angle(forwardBeforeTurn, GM.CurrentPlayerBody.transform.forward);
            if (turned < 30f || turned > 60f)
                throw new InvalidOperationException(
                    "Original TwinStick snap-turn did not rotate player heading by approximately " +
                    "45 degrees. angle=" + turned.ToString("F1"));
            mark("movement-snap-turn-pass", "headingDelta=" + turned.ToString("F1") + "deg");
            DriveNeutral(drive);
            mark("movement-pass", "original action input drove TwinStick locomotion and " +
                                      "snap-turn; transient options restored");
        } finally {
            try {
                DriveNeutral(drive);
            } finally {
                movement.Mode = oldMode;
                options.CurrentMovementMode = oldCurrentMode;
                options.TwinStickLeftRightState = oldHandSetup;
                options.Touchpad_MovementMode = oldMovementBasis;
                options.TwinStickSnapturnState = oldSnapMode;
                options.SnapTurnMagnitudeIndex = oldSnapIndex;
                options.AXButtonSnapTurnState = oldAxButtonMode;
                options.TwinStickSprintState = oldSprintMode;
                movement.CleanupFlagsForModeSwitch();
            }
        }
    }
}
