using System;
using System.Collections;
using System.Linq;
using FistVR;
using UnityEngine;
using Valve.VR;

// Exercises the game's existing player quickbelt with real tracked-hand input.
// It observes only serialized/runtime state and drives the controller pose and
// grip action; slot creation, hover, storage, and retrieval remain original code.
public static class NativePlayerQuickbeltFixture {
    const uint LeftRole = 2;
    const uint RightRole = 3;
    const float MotionObservationSeconds = 2.0f;

    public static IEnumerator Run(NativePlayerHandDriver drive, Action<string, string> mark) {
        if (drive == null)
            throw new ArgumentNullException("drive");
        if (GM.CurrentPlayerBody == null || GM.CurrentSceneSettings == null)
            throw new InvalidOperationException("Quickbelt fixture requires an initialized " +
                                                "original player body and scene settings.");

        var body = GM.CurrentPlayerBody;
        if (!GM.CurrentSceneSettings.AreQuickbeltSlotsEnabled)
            throw new InvalidOperationException(
                "Current FVRSceneSettings disables original quickbelt slots.");
        if (body.QBSlots_Internal == null || body.QBSlots_Internal.Count == 0)
            throw new InvalidOperationException(
                "Original player initialization produced no QBSlots_Internal entries.");

        var renderer = UnityEngine.Object.FindObjectOfType<NativeH3VR.NativeStereoRenderer>();
        if (renderer == null || renderer.trackingOrigin == null)
            throw new InvalidOperationException("Native stereo tracking origin is unavailable.");

        var hand = UnityEngine.Object.FindObjectsOfType<FVRViveHand>().FirstOrDefault(
            h => h.HandSource == SteamVR_Input_Sources.RightHand);
        if (hand == null || hand.PoseOverride == null)
            throw new InvalidOperationException(
                "Original right hand or its PoseOverride is missing.");

        var held = hand.CurrentInteractable as FVRPhysicalObject;
        if (held == null || !held.IsHeld || held.m_hand != hand)
            throw new InvalidOperationException(
                "Invoke this fixture while a genuine FVRPhysicalObject is held by the right hand.");

        var camera = body.EyeCam != null ? body.EyeCam.transform : null;
        var head = body.Head;
        if (camera == null || head == null || body.Torso == null)
            throw new InvalidOperationException(
                "Player rig is missing EyeCam, Head, or Torso references.");

        Vector3 cameraStart = camera.position;
        Vector3 headStart = head.position;
        Vector3 torsoStart = body.Torso.position;
        Vector3 nativeHeadStart;
        bool hasNativeHeadStart =
            TryGetNativeHeadWorld(renderer.trackingOrigin, out nativeHeadStart);
        float observedHeadMotion = 0f;
        float observedCameraMotion = 0f;
        float observedTorsoMotion = 0f;
        float observationEnd = Time.realtimeSinceStartup + MotionObservationSeconds;
        while (Time.realtimeSinceStartup < observationEnd) {
            Vector3 nativeHead;
            if (hasNativeHeadStart &&
                TryGetNativeHeadWorld(renderer.trackingOrigin, out nativeHead))
                observedHeadMotion =
                    Mathf.Max(observedHeadMotion, Vector3.Distance(nativeHeadStart, nativeHead));
            observedCameraMotion =
                Mathf.Max(observedCameraMotion, Vector3.Distance(cameraStart, camera.position));
            observedTorsoMotion =
                Mathf.Max(observedTorsoMotion, Vector3.Distance(torsoStart, body.Torso.position));
            yield return null;
        }

        string rig =
            "cameraHeadDelta=" + Vector3.Distance(camera.position, head.position).ToString("F4") +
            " nativeHeadMotion=" + observedHeadMotion.ToString("F4") +
            " eyeCameraMotion=" + observedCameraMotion.ToString("F4") +
            " playerHeadMotion=" + Vector3.Distance(headStart, head.position).ToString("F4") +
            " torsoMotion=" + observedTorsoMotion.ToString("F4") +
            " internalSlots=" + body.QBSlots_Internal.Count;
        Emit(mark, "player-rig-observation", rig);

        // Eye-camera transform is the original SteamVR_Camera head after its
        // OnEnable rig expansion. If OpenXR head motion advances while the eye
        // camera remains static, gameplay's head/body consumers are stale even
        // though the camera is briefly moved during each eye render.
        if (observedHeadMotion > 0.08f && observedCameraMotion < 0.02f)
            throw new InvalidOperationException(
                "OpenXR head moved but original eye/head transform did not follow outside eye " +
                "rendering; camera/head-dependent gameplay is stale.");

        var slot = body.QBSlots_Internal
                       .Where(s => s != null && s.IsPlayer && s.IsSelectable &&
                                   s.gameObject.activeInHierarchy && s.PoseOverride != null &&
                                   s.HoverGeo != null && s.CurObject == null &&
                                   s.Type == held.QBSlotType && s.SizeLimit >= held.Size)
                       .OrderBy(s => Vector3.Distance(s.HoverGeo.transform.position,
                                                      hand.PoseOverride.position))
                       .FirstOrDefault();
        if (slot == null)
            throw new InvalidOperationException(
                "No active, empty original player slot accepts the held item's type=" +
                held.QBSlotType + " size=" + held.Size + ".");

        uint role = hand.HandSource == SteamVR_Input_Sources.LeftHand ? LeftRole : RightRole;
        Quaternion handRotation = hand.transform.rotation;
        Vector3 initialControllerRoot = hand.transform.position;
        Vector3 handPoseOffset = hand.transform.InverseTransformPoint(hand.PoseOverride.position);
        Vector3 heldPoseOffset = hand.transform.InverseTransformPoint(
            held.PoseOverride != null ? held.PoseOverride.position : held.transform.position);
        Vector3 initialTrackingLocal =
            renderer.trackingOrigin.InverseTransformPoint(initialControllerRoot);
        Quaternion initialTrackingRotation =
            Quaternion.Inverse(renderer.trackingOrigin.rotation) * handRotation;

        Emit(mark, "quickbelt-slot-selected",
             "name=" + slot.name + " type=" + slot.Type + " size=" + slot.SizeLimit +
                 " objectType=" + held.QBSlotType + " objectSize=" + held.Size + " hoverCenter=" +
                 slot.HoverGeo.transform.position + " itemPose=" + slot.PoseOverride.position +
                 " localPosition=" + slot.transform.localPosition + " torso=" + body.Torso.name);

        // Put the held item's actual interaction point inside the target volume.
        // TestQuickBeltDistances uses that item point while the hand is holding it.
        Vector3 storeRoot = slot.HoverGeo.transform.position - handRotation * heldPoseOffset;
        float hoverDeadline = Time.realtimeSinceStartup + 5f;
        int stableStoreHoverFrames = 0;
        while (stableStoreHoverFrames < 2 && Time.realtimeSinceStartup < hoverDeadline) {
            storeRoot = slot.HoverGeo.transform.position - handRotation * heldPoseOffset;
            Drive(drive, role, storeRoot, handRotation, 1f);
            yield return null;
            stableStoreHoverFrames =
                hand.CurrentHoveredQuickbeltSlot == slot ? stableStoreHoverFrames + 1 : 0;
        }
        if (stableStoreHoverFrames < 2)
            throw new InvalidOperationException(
                "Original hand hover did not select compatible slot at its PoseOverride; point=" +
                (held.PoseOverride != null ? held.PoseOverride.position : held.transform.position) +
                " target=" + slot.HoverGeo.transform.position + " held=" + held.name +
                " state=" + hand.CurrentInteractable);

        // Release through the original FVRViveHand Update path. The update owns
        // compatibility checks and EndInteractionIntoInventorySlot.
        Drive(drive, role, storeRoot, handRotation, 0f);
        float storeDeadline = Time.realtimeSinceStartup + 5f;
        while ((slot.CurObject != held || held.QuickbeltSlot != slot) &&
               Time.realtimeSinceStartup < storeDeadline)
            yield return null;
        if (slot.CurObject != held || held.QuickbeltSlot != slot || held.IsHeld)
            throw new InvalidOperationException(
                "Original open-hand release did not store the object into the selected slot.");
        Emit(mark, "quickbelt-original-store",
             "slot=" + slot.name + " object=" + held.name +
                 " currentInteractable=" + hand.CurrentInteractable);

        // Move the empty hand's own PoseOverride into the occupied slot and use
        // the original grip edge. FVRViveHand.Update retrieves the slot object.
        Vector3 retrieveRoot = slot.HoverGeo.transform.position - handRotation * handPoseOffset;
        float retrieveHoverDeadline = Time.realtimeSinceStartup + 5f;
        int stableRetrieveHoverFrames = 0;
        while (stableRetrieveHoverFrames < 2 && Time.realtimeSinceStartup < retrieveHoverDeadline) {
            retrieveRoot = slot.HoverGeo.transform.position - handRotation * handPoseOffset;
            Drive(drive, role, retrieveRoot, handRotation, 0f);
            yield return null;
            stableRetrieveHoverFrames =
                hand.CurrentHoveredQuickbeltSlot == slot && hand.CurrentInteractable == null
                    ? stableRetrieveHoverFrames + 1
                    : 0;
        }
        if (stableRetrieveHoverFrames < 2 || hand.CurrentHoveredQuickbeltSlot != slot ||
            hand.CurrentInteractable != null)
            throw new InvalidOperationException("Original neutral hand hover did not stabilize " +
                                                "over the occupied slot before grip input.");
        // Send the grip edge only after the original update loop has observed
        // the slot hover on consecutive frames. Continue driving the pose and
        // grip while the interaction starts so a one-frame input race cannot
        // consume the pickup edge before TestQuickBeltDistances runs.
        Drive(drive, role, retrieveRoot, handRotation, 1f);
        float retrieveDeadline = Time.realtimeSinceStartup + 5f;
        while (hand.CurrentInteractable != held && Time.realtimeSinceStartup < retrieveDeadline) {
            retrieveRoot = slot.HoverGeo.transform.position - handRotation * handPoseOffset;
            Drive(drive, role, retrieveRoot, handRotation, 1f);
            yield return null;
        }
        if (hand.CurrentInteractable != held || !held.IsHeld || held.m_hand != hand)
            throw new InvalidOperationException(
                "Original grip path did not retrieve the item from the occupied slot.");
        heldPoseOffset = hand.transform.InverseTransformPoint(
            held.PoseOverride != null ? held.PoseOverride.position : held.transform.position);
        Emit(mark, "quickbelt-original-retrieve",
             "slot=" + slot.name + " object=" + held.name + " held=" + held.IsHeld +
                 " hand=" + held.m_hand.HandSource);

        // Restore the original belt state using the same release path, leaving
        // the test item safely stored rather than loose or destroyed.
        Vector3 restowRoot = slot.HoverGeo.transform.position - handRotation * heldPoseOffset;
        float restowHoverDeadline = Time.realtimeSinceStartup + 5f;
        int stableRestowHoverFrames = 0;
        while (stableRestowHoverFrames < 2 && Time.realtimeSinceStartup < restowHoverDeadline) {
            restowRoot = slot.HoverGeo.transform.position - handRotation * heldPoseOffset;
            Drive(drive, role, restowRoot, handRotation, 1f);
            yield return null;
            stableRestowHoverFrames =
                hand.CurrentHoveredQuickbeltSlot == slot ? stableRestowHoverFrames + 1 : 0;
        }
        if (stableRestowHoverFrames < 2)
            throw new InvalidOperationException(
                "Retrieved item could not re-enter its original slot volume.");
        Drive(drive, role, restowRoot, handRotation, 0f);
        float restowDeadline = Time.realtimeSinceStartup + 5f;
        while ((slot.CurObject != held || held.QuickbeltSlot != slot) &&
               Time.realtimeSinceStartup < restowDeadline)
            yield return null;
        if (slot.CurObject != held || held.QuickbeltSlot != slot || held.IsHeld)
            throw new InvalidOperationException(
                "Original release path did not restore the retrieved item to its slot.");

        // Leave controller input neutral at the captured initial pose. Parent
        // probe owns subsequent cleanup and reports the end of this fixture.
        DriveLocal(drive, role, initialTrackingLocal, initialTrackingRotation, 0f);
        Emit(mark, "quickbelt-roundtrip-pass",
             "slot=" + slot.name + " object=" + held.name +
                 " headMotionObserved=" + observedHeadMotion.ToString("F4") +
                 " torsoMotionObserved=" + observedTorsoMotion.ToString("F4"));
    }

    static bool TryGetNativeHeadWorld(Transform origin, out Vector3 world) {
        NativeH3VR.XRBridge.Pose pose;
        if (NativeH3VR.XRBridge.nh3vr_xr_head_pose(out pose) == 0) {
            world = origin.TransformPoint(pose.position);
            return true;
        }
        world = Vector3.zero;
        return false;
    }

    static void Drive(NativePlayerHandDriver drive, uint role, Vector3 worldPosition,
                      Quaternion worldRotation, float grip) {
        drive(role, worldPosition, worldRotation, 0f, grip, 0, 0, Vector2.zero, Vector2.zero);
    }

    static void DriveLocal(NativePlayerHandDriver drive, uint role, Vector3 localPosition,
                           Quaternion localRotation, float grip) {
        // NativePlayerHandDriver accepts a world raw controller root; reconstruct
        // it from the current origin each call so recenter/origin motion applies.
        var renderer = UnityEngine.Object.FindObjectOfType<NativeH3VR.NativeStereoRenderer>();
        if (renderer == null || renderer.trackingOrigin == null)
            return;
        Drive(drive, role, renderer.trackingOrigin.TransformPoint(localPosition),
              renderer.trackingOrigin.rotation * localRotation, grip);
    }

    static void Emit(Action<string, string> mark, string stage, string detail) {
        if (mark != null)
            mark(stage, detail);
        else
            Debug.Log("NATIVE_PLAYER_QUICKBELT " + stage + " " + detail);
    }
}
