using System;
using System.Collections;
using System.Linq;
using FistVR;
using NativeH3VR;
using UnityEngine;
using Valve.VR;

/// <summary>
/// Drives the original in-scene wrist menu through native controller poses
/// and trigger input. It does not invoke menu activation, selection, or button
/// callbacks directly.
/// </summary>
public static class NativePlayerWristFixture {
    const float TimeoutSeconds = 12f;
    const float HeadPositionTolerance = .08f;
    const float HeadRotationTolerance = 8f;

    public static IEnumerator Run(NativePlayerHandDriver drive, Action<string, string> mark) {
        if (drive == null)
            throw new ArgumentNullException("drive");
        if (mark == null)
            throw new ArgumentNullException("mark");

        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        FVRPlayerBody body = null;
        FVRWristMenu2 menu = null;
        while (Time.realtimeSinceStartup < deadline && (body == null || menu == null)) {
            body = GM.CurrentPlayerBody;
            menu = UnityEngine.Object.FindObjectsOfType<FVRWristMenu2>().FirstOrDefault();
            yield return null;
        }
        if (body == null || menu == null)
            throw new InvalidOperationException(
                "Original player body or wrist menu did not appear");
        if (body.EyeCam == null || menu.Face != body.EyeCam.transform)
            throw new InvalidOperationException(
                "Original wrist menu Face is not the live player EyeCam transform");
        if (menu.MenuGO == null || menu.Hands == null || menu.Hands.Length < 2)
            throw new InvalidOperationException(
                "Original wrist menu is missing its menu object or hand references");
        if (menu.MenuGO.activeSelf)
            throw new InvalidOperationException(
                "Wrist fixture requires the original menu to start closed");

        FVRViveHand wrist = SelectWristHand(menu);
        FVRViveHand pointer = wrist.OtherHand;
        uint wristRole = Role(wrist), pointerRole = Role(pointer);
        Transform wristRoot = PoseRoot(wrist), pointerRoot = PoseRoot(pointer);
        Transform wristTarget = wrist.GetWristMenuTarget();
        Transform pointerTarget = pointer.PointingTransform;
        RequireChild(wristRoot, wristTarget, "Index wrist target");
        RequireChild(pointerRoot, pointerTarget, "pointing target");
        if (wrist.CurrentInteractable != null || pointer.CurrentInteractable != null)
            throw new InvalidOperationException("Wrist fixture requires both hands to be empty");

        if (!NativeDiagnosticInput.Active)
            throw new InvalidOperationException(
                "Parent wrist fixture driver must start explicit diagnostic input before Run");
        DriveNeutral(drive);
        float neutralDeadline = Time.realtimeSinceStartup + 2f;
        while (Time.realtimeSinceStartup < neutralDeadline) {
            AssertHeadTracksEyeCamera(body);
            yield return null;
        }
        mark("wrist-face-bound", "face=EyeCam headSync=within-" + HeadPositionTolerance + "m/" +
                                     HeadRotationTolerance + "deg");

        // The real game opens the menu when an empty hand presents its wrist
        // toward the face. Keep the requested pose relative to the current
        // camera transform so the test also exercises a moving head reference.
        deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (!menu.MenuGO.activeSelf && Time.realtimeSinceStartup < deadline) {
            AssertHeadTracksEyeCamera(body);
            DriveWristGesture(drive, wristRole, wristRoot, wristTarget, menu.Face);
            DriveAtCurrent(drive, pointerRole, pointerRoot, pointerTarget, 0f, 0f);
            yield return null;
        }
        if (!menu.MenuGO.activeSelf || menu.GetCurrentHand() != wrist)
            throw new InvalidOperationException(
                "Original wrist gesture did not activate the menu on the selected hand");
        mark("wrist-gesture-open", "hand=" + wrist.HandSource + " target=" + wristTarget.name);

        FVRWristMenuSection_MoveMode moveMode =
            menu.Sections.OfType<FVRWristMenuSection_MoveMode>().FirstOrDefault(
                s => s != null && s.IsAvailable());
        if (moveMode == null)
            throw new InvalidOperationException(
                "No harmless original locomotion section is available to verify menu clicks");
        int sectionIndex = menu.Sections.IndexOf(moveMode);
        if (menu.SectionSet == null || menu.SectionSet.ButtonsInSet == null ||
            sectionIndex >= menu.SectionSet.ButtonsInSet.Length)
            throw new InvalidOperationException(
                "Original wrist menu has no pointable section button for locomotion");
        FVRPointableButton sectionButton = menu.SectionSet.ButtonsInSet[sectionIndex];
        Collider buttonCollider =
            sectionButton == null ? null : sectionButton.GetComponent<Collider>();
        if (buttonCollider == null && sectionButton != null)
            buttonCollider = sectionButton.GetComponentInChildren<Collider>();
        if (buttonCollider == null)
            throw new InvalidOperationException(
                "Locomotion section has no original raycast collider");

        // Aim the other hand through the real FVR pointable ray and wait for
        // the game to report that collider as CurrentPointable before pressing.
        deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (pointer.CurrentPointable != sectionButton && Time.realtimeSinceStartup < deadline) {
            AssertHeadTracksEyeCamera(body);
            DriveWristGesture(drive, wristRole, wristRoot, wristTarget, menu.Face);
            DriveAtCurrent(drive, pointerRole, pointerRoot, pointerTarget, 0f, 0f);
            AimTarget(drive, pointerRole, pointerRoot, pointerTarget, buttonCollider.bounds.center);
            yield return null;
        }
        if (pointer.CurrentPointable != sectionButton)
            throw new InvalidOperationException(
                "Original pointing ray did not hover the locomotion section collider");
        mark("wrist-button-hover", "section=MoveMode pointable=" + sectionButton.name);

        DriveWristGesture(drive, wristRole, wristRoot, wristTarget, menu.Face);
        bool triggerDownObserved = false;
        deadline = Time.realtimeSinceStartup + 4f;
        while (!moveMode.gameObject.activeSelf && Time.realtimeSinceStartup < deadline) {
            AssertHeadTracksEyeCamera(body);
            DriveWristGesture(drive, wristRole, wristRoot, wristTarget, menu.Face);
            if (pointer.Input.TriggerDown)
                triggerDownObserved = true;
            AimTarget(drive, pointerRole, pointerRoot, pointerTarget, buttonCollider.bounds.center,
                      1f);
            yield return null;
        }
        AimTarget(drive, pointerRole, pointerRoot, pointerTarget, buttonCollider.bounds.center, 0f);
        if (!moveMode.gameObject.activeSelf)
            throw new InvalidOperationException(
                "Original trigger press did not open the locomotion section");
        mark("wrist-button-click",
             "section=MoveMode via=FVRPointableButton.OnPoint triggerDownObserved=" +
                 triggerDownObserved);

        // Present the same hand away from the face; the original Update loop
        // must close the menu without calling Deactivate directly.
        deadline = Time.realtimeSinceStartup + 4f;
        while (menu.MenuGO.activeSelf && Time.realtimeSinceStartup < deadline) {
            AssertHeadTracksEyeCamera(body);
            Quaternion away = Quaternion.LookRotation(menu.Face.forward, menu.Face.up);
            DriveTargetPose(drive, wristRole, wristRoot, wristTarget,
                            menu.Face.position + menu.Face.forward * .3f, away, 0f, 0f);
            DriveNeutralHand(drive, pointerRole, pointerRoot);
            yield return null;
        }
        if (menu.MenuGO.activeSelf)
            throw new InvalidOperationException(
                "Original wrist pose-away condition did not deactivate the menu");
        DriveNeutral(drive);
        mark("wrist-gesture-close", "reason=original-pose-away-condition");
    }

    static FVRViveHand SelectWristHand(FVRWristMenu2 menu) {
        ControlOptions.WristMenuMode mode = GM.Options.ControlOptions.WristMenuState;
        return menu.Hands.FirstOrDefault(
                   h => h != null &&
                        (mode == ControlOptions.WristMenuMode.BothHands ||
                         (mode == ControlOptions.WristMenuMode.RightHand && h.IsThisTheRightHand) ||
                         (mode == ControlOptions.WristMenuMode.LeftHand &&
                          !h.IsThisTheRightHand))) ??
               throw new InvalidOperationException("Saved WristMenuState excludes both menu hands");
    }

    static uint Role(FVRViveHand hand) {
        if (hand.HandSource == SteamVR_Input_Sources.LeftHand)
            return 2;
        if (hand.HandSource == SteamVR_Input_Sources.RightHand)
            return 3;
        throw new InvalidOperationException("Player hand has unexpected SteamVR source " +
                                            hand.HandSource);
    }

    static Transform PoseRoot(FVRViveHand hand) {
        SteamVR_Behaviour_Pose pose = hand.GetComponent<SteamVR_Behaviour_Pose>() ??
                                      hand.GetComponentInParent<SteamVR_Behaviour_Pose>() ??
                                      hand.GetComponentInChildren<SteamVR_Behaviour_Pose>(true);
        if (pose == null)
            throw new InvalidOperationException(
                "Hand has no original SteamVR_Behaviour_Pose transform: " + hand.name);
        return pose.transform;
    }

    static void RequireChild(Transform root, Transform target, string label) {
        if (target == null || !target.IsChildOf(root))
            throw new InvalidOperationException(
                label + " is not parented beneath its SteamVR raw-pose root");
    }

    static void DriveNeutral(NativePlayerHandDriver drive) {
        NativeStereoRenderer renderer = UnityEngine.Object.FindObjectOfType<NativeStereoRenderer>();
        if (renderer == null || renderer.trackingOrigin == null)
            throw new InvalidOperationException(
                "Neutral controller pose requires the active native tracking origin");
        Transform origin = renderer.trackingOrigin;
        Vector3 leftLocal = new Vector3(-.25f, 1.2f, .5f);
        Vector3 rightLocal = new Vector3(.25f, 1.2f, .5f);
        drive(2, origin.TransformPoint(leftLocal), origin.rotation, 0f, 0f, 0, 0, Vector2.zero,
              Vector2.zero);
        drive(3, origin.TransformPoint(rightLocal), origin.rotation, 0f, 0f, 0, 0, Vector2.zero,
              Vector2.zero);
    }

    static void DriveNeutralHand(NativePlayerHandDriver drive, uint role, Transform root) {
        if (root == null)
            throw new ArgumentNullException("root");
        drive(role, root.position, root.rotation, 0f, 0f, 0, 0, Vector2.zero, Vector2.zero);
    }

    static void DriveAtCurrent(NativePlayerHandDriver drive, uint role, Transform root,
                               Transform target, float trigger, float grip) {
        drive(role, root.position, root.rotation, trigger, grip, 0, 0, Vector2.zero, Vector2.zero);
    }

    static void DriveWristGesture(NativePlayerHandDriver drive, uint role, Transform root,
                                  Transform target, Transform face) {
        Quaternion rotation = Quaternion.LookRotation(-face.forward, face.up);
        DriveTargetPose(drive, role, root, target, face.position + face.forward * .3f, rotation, 0f,
                        0f);
    }

    static void AimTarget(NativePlayerHandDriver drive, uint role, Transform root, Transform target,
                          Vector3 point, float trigger = 0f) {
        Vector3 direction = point - target.position;
        if (direction.sqrMagnitude < .0001f)
            direction = target.forward;
        Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        DriveTargetPose(drive, role, root, target, point - direction.normalized * .25f, rotation,
                        trigger, 0f);
    }

    static void DriveTargetPose(NativePlayerHandDriver drive, uint role, Transform root,
                                Transform target, Vector3 targetPosition, Quaternion targetRotation,
                                float trigger, float grip) {
        Vector3 localPosition = root.InverseTransformPoint(target.position);
        Quaternion localRotation = Quaternion.Inverse(root.rotation) * target.rotation;
        Quaternion rootRotation = targetRotation * Quaternion.Inverse(localRotation);
        Vector3 rootPosition = targetPosition - rootRotation * localPosition;
        drive(role, rootPosition, rootRotation, trigger, grip, 0, 0, Vector2.zero, Vector2.zero);
    }

    static void AssertHeadTracksEyeCamera(FVRPlayerBody body) {
        SteamVR_Camera steamCamera = body.EyeCam.GetComponent<SteamVR_Camera>();
        if (steamCamera == null || steamCamera.origin == null)
            throw new InvalidOperationException(
                "EyeCam has no original SteamVR_Camera tracking origin");
        XRBridge.Pose nativeHead;
        int result = XRBridge.nh3vr_xr_head_pose(out nativeHead);
        if (result != 0)
            throw new InvalidOperationException(
                "Native XR head pose unavailable during wrist fixture: " + result);
        Transform eye = body.EyeCam.transform;
        Vector3 expectedPosition = steamCamera.origin.TransformPoint(nativeHead.position);
        Quaternion expectedRotation = steamCamera.origin.rotation * nativeHead.rotation;
        float positionError = Vector3.Distance(eye.position, expectedPosition);
        float rotationError = Quaternion.Angle(eye.rotation, expectedRotation);
        if (positionError > HeadPositionTolerance || rotationError > HeadRotationTolerance)
            throw new InvalidOperationException(
                "EyeCam gameplay transform is stale from native head pose: positionError=" +
                positionError + "m rotationError=" + rotationError +
                "deg origin=" + steamCamera.origin.name);
    }
}
