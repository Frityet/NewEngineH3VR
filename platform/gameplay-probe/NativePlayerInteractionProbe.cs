using System;
using System.Collections;
using System.Linq;
using FistVR;
using NativeH3VR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.VR;

// Explicit synthetic route through the original wrist and inventory systems.
// Installed by the existing gameplay probe hook, never in the live demo.
public sealed class NativePlayerInteractionProbe : MonoBehaviour {
    struct HandState {
        public Vector3 position;
        public Quaternion rotation;
        public float trigger, grip;
        public uint buttons, touches;
    }
    HandState[] hands = new HandState[2];
    NativeStereoRenderer renderer;
    float deadline;
    bool finished;
    string stage = "startup";
    void Update() {
        if (finished)
            return;
        if (deadline > 0 && Time.realtimeSinceStartup > deadline) {
            Fail("Timeout at " + stage);
            return;
        }
        if (!NativeDiagnosticInput.Active)
            return;
        for (int i = 0; i < 2; i++) {
            var h = hands[i];
            NativeDiagnosticInput.SetHand((uint)i + 2, h.position, h.rotation, h.trigger, h.grip,
                                          h.buttons, h.touches);
        }
    }
    void Drive(uint role, Vector3 worldPosition, Quaternion worldRotation, float trigger,
               float grip, uint buttons, uint touches, Vector2 primary, Vector2 secondary) {
        if (role < 2 || role > 3 || renderer == null || renderer.trackingOrigin == null)
            throw new InvalidOperationException("Invalid player fixture driver target");
        if (primary != Vector2.zero || secondary != Vector2.zero)
            throw new InvalidOperationException(
                "Axis fixture is not enabled for the wrist/quickbelt route");
        hands[role - 2] =
            new HandState { position = renderer.trackingOrigin.InverseTransformPoint(worldPosition),
                            rotation = Quaternion.Inverse(renderer.trackingOrigin.rotation) *
                                       worldRotation,
                            trigger = trigger,
                            grip = grip,
                            buttons = buttons,
                            touches = touches };
    }
    void Neutral() {
        for (int i = 0; i < 2; i++)
            hands[i] = new HandState { position = new Vector3(i == 0 ? -.25f : .25f, 1.25f, .55f),
                                       rotation = Quaternion.identity };
    }
    void Mark(string name, string detail = "") {
        stage = name;
        Debug.Log("NATIVE_GAMEPLAY_STAGE " + name + " " + detail);
    }
    IEnumerator Start() {
        deadline = Time.realtimeSinceStartup + 180;
        var run = Run();
        while (!finished) {
            bool next = false;
            Exception failure = null;
            try {
                next = run.MoveNext();
            } catch (Exception e) {
                failure = e;
            }
            if (failure != null) {
                Fail(failure.ToString());
                yield break;
            }
            if (!next)
                yield break;
            yield return run.Current;
        }
    }
    IEnumerator Run() {
        Mark("wait-mainmenu");
        while (SceneManager.GetActiveScene().name != "MainMenu3" ||
               FindObjectOfType<IM>() == null || !NativeSteamVRSystem.Running)
            yield return null;
        while (IM.OD == null || IM.OD.Count == 0 ||
               (GM.LoadingCallback != null && !GM.LoadingCallback.IsCompleted))
            yield return null;
        renderer = FindObjectOfType<NativeStereoRenderer>();
        while (renderer == null || renderer.completedFrames == 0 || renderer.sourceCamera == null) {
            renderer = FindObjectOfType<NativeStereoRenderer>();
            yield return null;
        }
        ulong before = renderer.completedFrames;
        const string target = "IndoorRange_Updated";
        Mark("original-scene-transition", target);
        SteamVR_LoadLevel.Begin(target);
        while (SceneManager.GetActiveScene().name != target || SteamVR_LoadLevel.loading ||
               GM.CurrentSceneSettings == null)
            yield return null;
        while (renderer.sourceCamera == null ||
               renderer.sourceCamera.gameObject.scene.name != target ||
               renderer.suspendSceneRendering || renderer.completedFrames <= before + 3)
            yield return null;
        Mark("native-camera-handoff", "frames=" + before + "->" + renderer.completedFrames +
                                          " camera=" + renderer.sourceCamera.name);
        Neutral();
        NativeDiagnosticInput.Begin();
        FVRViveHand left = null, right = null;
        while (left == null || right == null) {
            var all = FindObjectsOfType<FVRViveHand>();
            left = all.FirstOrDefault(h => h.HandSource == SteamVR_Input_Sources.LeftHand);
            right = all.FirstOrDefault(h => h.HandSource == SteamVR_Input_Sources.RightHand);
            yield return null;
        }
        while (!NativeSteamVRInput.InputFocused || !left.Pose[left.HandSource].poseIsValid ||
               !right.Pose[right.HandSource].poseIsValid || left.CMode != ControlMode.Index ||
               right.CMode != ControlMode.Index)
            yield return null;
        yield return null;
        yield return null;
        Mark("player-systems-ready", "head=" + GM.CurrentPlayerBody.Head.position + " eye=" +
                                         GM.CurrentPlayerBody.EyeCam.transform.position +
                                         " slots=" + GM.CurrentPlayerBody.QBSlots_Internal.Count +
                                         " mode=" + GM.CurrentMovementManager.Mode);
        var wrist = NativePlayerWristFixture.Run(Drive, Mark);
        while (wrist.MoveNext())
            yield return wrist.Current;
        Neutral();
        for (int i = 0; i < 5; i++)
            yield return null;
        FVRObject definition;
        if (!IM.OD.TryGetValue("M1911", out definition))
            throw new InvalidOperationException("Original M1911 definition absent");
        var request = definition.GetGameObjectAsync();
        while (!request.Pump())
            yield return null;
        var spawned = Instantiate(request.Result);
        var gun = spawned.GetComponentInChildren<FVRFireArm>();
        if (gun == null)
            throw new InvalidOperationException("Original Anvil M1911 has no FVRFireArm");
        var body = gun.RootRigidbody;
        bool gravity = body.useGravity, kinematic = body.isKinematic;
        body.useGravity = false;
        body.isKinematic = true;
        var gripPoint = gun.PoseOverride;
        var localRotation = gripPoint != null
                                ? Quaternion.Inverse(gun.transform.rotation) * gripPoint.rotation
                                : Quaternion.identity;
        gun.transform.rotation = right.transform.rotation * Quaternion.Inverse(localRotation);
        gun.transform.position = right.Display_InteractionSphere.transform.position;
        Physics.SyncTransforms();
        float until = Time.realtimeSinceStartup + 8;
        while (right.ClosestPossibleInteractable != gun && Time.realtimeSinceStartup < until)
            yield return null;
        if (right.ClosestPossibleInteractable != gun)
            throw new InvalidOperationException("Original hand hover did not select M1911");
        hands[1].grip = 1;
        until = Time.realtimeSinceStartup + 5;
        while (right.CurrentInteractable != gun && Time.realtimeSinceStartup < until)
            yield return null;
        if (right.CurrentInteractable != gun || !gun.IsHeld || gun.m_hand != right)
            throw new InvalidOperationException("Original grip input did not pick up M1911");
        body.useGravity = gravity;
        body.isKinematic = kinematic;
        Mark("original-firearm-held", "item=M1911 hand=RightHand");
        var quickbelt = NativePlayerQuickbeltFixture.Run(Drive, Mark);
        while (quickbelt.MoveNext())
            yield return quickbelt.Current;
        Neutral();
        for (int i = 0; i < 5; i++)
            yield return null;
        var capture = Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_SCREENSHOT");
        if (!string.IsNullOrEmpty(capture)) {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(capture);
            for (int i = 0; i < 5; i++)
                yield return null;
        }
        Mark("passed", "scene=" + target + " item=M1911 wrist=true quickbelt=true");
        Debug.Log("NATIVE_PLAYER_SYSTEMS_PASS wristGesture=true wristClick=true " +
                  "quickbeltStore=true quickbeltRetrieve=true quickbeltRestow=true");
        Debug.Log("NATIVE_GAMEPLAY_PROBE_PASS scene=" + target +
                  " item=M1911 shots=0 playerSystems=true");
        finished = true;
        NativeDiagnosticInput.End();
        Application.Quit(0);
    }
    void Fail(string message) {
        if (finished)
            return;
        finished = true;
        Debug.LogError("NATIVE_GAMEPLAY_PROBE_FAIL player-systems stage=" + stage + " " + message);
        NativeDiagnosticInput.End();
        Application.Quit(1);
    }
    void OnDestroy() {
        if (NativeDiagnosticInput.Active)
            NativeDiagnosticInput.End();
    }
}
