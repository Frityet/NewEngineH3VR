using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using FistVR;
using NativeH3VR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.VR;

// Explicit, isolated synthetic acceptance route. Does not substitute input,
// interaction or firearm implementations; the existing game handles all three.
public sealed class NativeGameplayProbe : MonoBehaviour {
    float deadline;
    bool finished;
    string stage = "startup";
    Vector3 handPosition = new Vector3(.25f, 1.25f, .55f);
    float trigger, grip;
    bool proximityHold;
    FVRSceneSettings shotScene;
    FVRSceneSettings.ShotFired shotListener;
    int shots;
    Vector3 shotMuzzlePosition;
    GameObject spawned;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install() {
        NativePerformanceProbe.LogConfiguration();
#if NEWENGINE_UNITY_2021
        NewEngineLiveProfile.Install();
#endif
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_PROBE") != "1")
            return;
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") != "synthetic" ||
            Environment.GetEnvironmentVariable("NATIVEH3VR_DIAGNOSTIC_INPUT") != "1")
            throw new InvalidOperationException(
                "Gameplay probe requires explicit synthetic OpenXR and diagnostic input flags.");
        var host = new GameObject("Native H3VR synthetic gameplay acceptance probe");
        DontDestroyOnLoad(host);
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_PLAYER_SYSTEMS") == "1")
            host.AddComponent<NativePlayerInteractionProbe>();
        else
            host.AddComponent<NativeGameplayProbe>();
    }
    void Update() {
        if (finished)
            return;
        if (deadline > 0 && Time.realtimeSinceStartup > deadline) {
            Fail("Timeout at " + stage);
            return;
        }
        if (NativeDiagnosticInput.Active) {
            NativeDiagnosticInput.SetHand(2, new Vector3(-.25f, 1.25f, .45f), Quaternion.identity,
                                          0, 0);
            NativeDiagnosticInput.SetHand(3, handPosition, Quaternion.identity, trigger, grip);
            if (proximityHold)
                NativeDiagnosticInput.SetFingerProximity(3, .8f, .7f, .6f, 0);
        }
    }
    IEnumerator Start() {
        deadline = Time.realtimeSinceStartup + 180;
        var test = Run();
        while (!finished) {
            bool next = false;
            Exception failure = null;
            try {
                next = test.MoveNext();
            } catch (Exception error) {
                failure = error;
            }
            if (failure != null) {
                Fail(failure.ToString());
                yield break;
            }
            if (!next)
                yield break;
            yield return test.Current;
        }
    }
    void Mark(string value, string detail = "") {
        stage = value;
        Debug.Log("NATIVE_GAMEPLAY_STAGE " + value + " " + detail);
    }
    IEnumerator Run() {
        Mark("wait-mainmenu");
        while (SceneManager.GetActiveScene().name != "MainMenu3" ||
               FindObjectOfType<IM>() == null || !NativeSteamVRSystem.Running)
            yield return null;
        while (IM.OD == null || IM.OD.Count == 0 ||
               (GM.LoadingCallback != null && !GM.LoadingCallback.IsCompleted))
            yield return null;
        Mark("mainmenu-ready",
             "items=" + IM.OD.Count + " model=" + NativeSteamVRBootstrap.HeadsetModel);
        var renderer = FindObjectOfType<NativeStereoRenderer>();
        if (renderer == null)
            throw new Exception("Main menu has no native stereo session renderer");
        while (renderer.completedFrames == 0 || renderer.sourceCamera == null)
            yield return null;
        ulong framesBefore = renderer.completedFrames;
        string target = Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_SCENE") ??
                        "IndoorRange_Updated";
        if (!Application.CanStreamedLevelBeLoaded(target))
            throw new Exception("Scene absent from built player: " + target);
        Mark("original-scene-transition", target);
        SteamVR_LoadLevel.Begin(target);
        while (SceneManager.GetActiveScene().name != target || SteamVR_LoadLevel.loading)
            yield return null;
        while (FindObjectOfType<IM>() == null || GM.CurrentSceneSettings == null)
            yield return null;
        Mark("scene-loaded", target + " roots=" + SceneManager.GetActiveScene().rootCount);
        while (renderer != null &&
               (renderer.sourceCamera == null ||
                renderer.sourceCamera.gameObject.scene.name != target ||
                renderer.suspendSceneRendering || renderer.completedFrames <= framesBefore))
            yield return null;
        if (renderer == null || FindObjectOfType<NativeStereoRenderer>() != renderer)
            throw new Exception("Scene transition replaced/lost the persistent stereo renderer");
        var cameraWitness =
            renderer.sourceCamera.gameObject.AddComponent<NativeGameplayCameraWitness>();
        while (cameraWitness.preCull < 2 || cameraWitness.postRender < 2)
            yield return null;
        Mark("native-camera-handoff", "frames=" + framesBefore + "->" + renderer.completedFrames +
                                          " camera=" + renderer.sourceCamera.name +
                                          " pre=" + cameraWitness.preCull +
                                          " post=" + cameraWitness.postRender);
        Destroy(cameraWitness);
        NativePerformanceProbe.RunOptionalModChecks();
#if NEWENGINE_UNITY_2021
        if (Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_PROBE") == "1") {
            string scopeOutput = Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_OUTPUT");
            if (string.IsNullOrEmpty(scopeOutput))
                throw new ArgumentException("Scope fixture requires NEWENGINEH3VR_SCOPE_OUTPUT");
            var scopeTest = NativeScopeProbe.Run(renderer, scopeOutput);
            while (scopeTest.MoveNext())
                yield return scopeTest.Current;
        }
#endif
        if (Environment.GetEnvironmentVariable("NEWENGINEH3VR_GRAPHICS_RECREATE_TEST") == "1") {
            var textureTest = NativeGraphicsTextureProbe.Run(renderer);
            while (textureTest.MoveNext())
                yield return textureTest.Current;
        }
        // Observe an original scene without assuming that every game mode is a
        // firearm sandbox. This uses the same real loading/fade/camera path.
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_SCENE_ONLY") == "1") {
            var performance = NativePerformanceProbe.Measure(renderer, target);
            while (performance.MoveNext())
                yield return performance.Current;
            ulong observedFrom = renderer.completedFrames;
            float until = Time.realtimeSinceStartup + 5;
            while (Time.realtimeSinceStartup < until)
                yield return null;
            if (renderer == null || renderer.sourceCamera == null ||
                renderer.sourceCamera.gameObject.scene.name != target ||
                renderer.suspendSceneRendering || renderer.completedFrames <= observedFrom + 2)
                throw new Exception("Original scene stopped rendering during observation: " +
                                    target);
            var capture = Capture(renderer, target);
            while (capture.MoveNext())
                yield return capture.Current;
            Debug.Log("NATIVE_SCENE_PROBE_PASS scene=" + target +
                      " observationSeconds=5 stereoFrames=" +
                      (renderer.completedFrames - observedFrom));
            finished = true;
            Cleanup();
            Application.Quit(0);
            yield break;
        }
        NativeDiagnosticInput.Begin();
        FVRViveHand hand = null;
        while (hand == null) {
            hand = FindObjectsOfType<FVRViveHand>().FirstOrDefault(
                h => h.HandSource == SteamVR_Input_Sources.RightHand);
            yield return null;
        }
        // InitCMode waits several startup frames before applying controller offsets.
        // A valid action pose alone can precede this original initialization.
        while (!hand.Pose[hand.HandSource].poseIsValid || hand.CMode != ControlMode.Index)
            yield return null;
        yield return null;
        yield return null;
        Mark("diagnostic-hand-ready",
             "mode=" + hand.CMode + " position=" + hand.transform.position);
        string item = Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_ITEM") ?? "M1911";
        FVRObject definition;
        if (!IM.OD.TryGetValue(item, out definition) ||
            definition.Category != FVRObject.ObjectCategory.Firearm)
            throw new Exception("Firearm definition absent: " + item);
        Mark("original-anvil-load", "item=" + item + " guid=" + definition.Guid);
        var request = definition.GetGameObjectAsync();
        while (!request.Pump())
            yield return null;
        var prefab = request.Result;
        if (prefab == null)
            throw new Exception("Original Anvil callback returned null firearm prefab");
        spawned = Instantiate(prefab);
        var gun = spawned.GetComponentInChildren<FVRFireArm>();
        if (gun == null)
            throw new Exception("Loaded prefab has no genuine FVRFireArm");
        var components = spawned.GetComponentsInChildren<Component>(true);
        if (components.Any(c => c == null))
            throw new Exception("Firearm prefab contains missing scripts");
        Debug.Log(
            "NATIVE_GAMEPLAY_BUNDLES " +
            string.Join(",", AssetBundle.GetAllLoadedAssetBundles().Select(b => b.name).ToArray()));
        var body = gun.RootRigidbody;
        bool gravity = body != null && body.useGravity,
             kinematic = body != null && body.isKinematic;
        // Hold the object still only while setting up the collision/grip test.
        // No RetrieveObject/BeginInteraction/ForceSetInteractable calls are used.
        if (body != null) {
            body.useGravity = false;
            body.isKinematic = true;
        }
        var gripPoint = gun.PoseOverride;
        Quaternion localRotation =
            gripPoint != null ? Quaternion.Inverse(gun.transform.rotation) * gripPoint.rotation
                              : Quaternion.identity;
        gun.transform.rotation = hand.transform.rotation * Quaternion.Inverse(localRotation);
        // Original FVRViveHand.TestCollider ranks each interactive object's
        // transform origin. PoseOverride is used after a grab, not for hover;
        // aligning it here can make a slide or bolt the closer interaction.
        gun.transform.position = hand.Display_InteractionSphere.transform.position;
        Physics.SyncTransforms();
        Mark("firearm-instantiated", "item=" + item + " class=" + gun.GetType().FullName +
                                         " components=" + components.Length + " renderers=" +
                                         spawned.GetComponentsInChildren<Renderer>(true).Length +
                                         " chambers=" + gun.GetChambers().Count);
        float hoverDeadline = Time.realtimeSinceStartup + 8;
        while (hand.ClosestPossibleInteractable != gun && Time.realtimeSinceStartup < hoverDeadline)
            yield return null;
        if (hand.ClosestPossibleInteractable != gun)
            throw new Exception(
                "Actual hand collision hover did not select loaded firearm; closest=" +
                hand.ClosestPossibleInteractable + " hand=" + hand.transform.position +
                " gun=" + gun.transform.position);
        Mark("original-grip-input");
        grip = 1;
        float gripDeadline = Time.realtimeSinceStartup + 5;
        while (hand.CurrentInteractable != gun && Time.realtimeSinceStartup < gripDeadline)
            yield return null;
        if (hand.CurrentInteractable != gun || !gun.IsHeld || gun.m_hand != hand)
            throw new Exception("Original grip path did not hold firearm");
        if (body != null) {
            body.useGravity = gravity;
            body.isKinematic = kinematic;
        }
        Mark("original-firearm-held", "hand=" + hand.HandSource);
        proximityHold = true;
        grip = 0;
        float holdDeadline = Time.realtimeSinceStartup + 1;
        while (Time.realtimeSinceStartup < holdDeadline) {
            if (hand.CurrentInteractable != gun || !gun.IsHeld || gun.m_hand != hand)
                throw new Exception("Original firearm hold failed after grip force release");
            yield return null;
        }
        if (hand.Input.GripPressed || hand.Input.GripTouched || hand.Input.TriggerTouched ||
            hand.Input.FingerCurl_Middle < .79f || hand.Input.FingerCurl_Ring < .69f)
            throw new Exception(
                "Proximity hold did not traverse original estimated skeletal input");
        Mark("original-proximity-hold", "middle=" + hand.Input.FingerCurl_Middle + " ring=" +
                                            hand.Input.FingerCurl_Ring + " forceReleased=true");
        bool fire = Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_FIRE") == "1";
        HashSet<int> previousProjectiles = null;
        string projectileName = null;
        int expectedProjectiles = 0;
        if (fire) {
            var chamber = NativeGameplayLoadFixture.PrepareOneShot(gun);
            if (!chamber.IsFull || chamber.IsSpent)
                throw new Exception("Original ammo/chamber load failed");
            var round = chamber.GetRound();
            if (round == null || round.NumProjectiles <= 0 ||
                round.BallisticProjectilePrefab == null ||
                round.BallisticProjectilePrefab.GetComponent<BallisticProjectile>() == null)
                throw new Exception(
                    "Original live round has no valid ballistic projectile payload");
            expectedProjectiles = round.NumProjectiles;
            projectileName = round.BallisticProjectilePrefab.name + "(Clone)";
            previousProjectiles = new HashSet<int>(
                FindObjectsOfType<BallisticProjectile>().Select(p => p.GetInstanceID()));
            shotScene = GM.CurrentSceneSettings;
            shotListener = f => {
                if (f == gun) {
                    shots++;
                    shotMuzzlePosition = gun.GetMuzzle().position;
                }
            };
            shotScene.ShotFiredEvent += shotListener;
            Mark("original-live-round-loaded", "round=" + gun.RoundType);
        }
        Mark("original-trigger-input", fire ? "one simulated live-round shot" : "dry trigger");
        trigger = 1;
        float triggerDeadline = Time.realtimeSinceStartup + 5;
        while (
            (!hand.Input.TriggerPressed || hand.Input.TriggerFloat < .9f || (fire && shots == 0)) &&
            Time.realtimeSinceStartup < triggerDeadline)
            yield return null;
        if (!hand.Input.TriggerPressed || hand.Input.TriggerFloat < .9f || (fire && shots == 0))
            throw new Exception("Original firearm trigger path failed; shots=" + shots);
        if (fire && shots != 1)
            throw new Exception("Expected exactly one original shot event, got " + shots);
        // Release the trigger after the accepted shot while retaining the grip.
        // Projectile/capture observations must not keep an automatic trigger held.
        trigger = 0;
        if (fire) {
            // OnShotFired runs BEFORE projectile creation in original FVRFireArm.Fire.
            // Observe original objects and private trajectory state read-only; do
            // not attach a fake projectile, call Fire or change projectile fields.
            var originField = ProjectileField("m_startPoint", typeof(Vector3));
            var velocityField = ProjectileField("m_initialMuzzleVelocity", typeof(float));
            var distanceField = ProjectileField("m_distanceTraveled", typeof(float));
            var observed = new HashSet<int>();
            float projectileDeadline = Time.realtimeSinceStartup + 5;
            while (observed.Count < expectedProjectiles &&
                   Time.realtimeSinceStartup < projectileDeadline) {
                foreach (var projectile in FindObjectsOfType<BallisticProjectile>()) {
                    int id = projectile.GetInstanceID();
                    if (previousProjectiles.Contains(id) || observed.Contains(id) ||
                        projectile.name != projectileName)
                        continue;
                    var origin = (Vector3)originField.GetValue(projectile);
                    float velocity = (float)velocityField.GetValue(projectile),
                          distance = (float)distanceField.GetValue(projectile);
                    if (!NativeGameplayBallisticEvidence.Accept(
                            true, Vector3.Distance(origin, shotMuzzlePosition), velocity, distance))
                        continue;
                    observed.Add(id);
                    Mark("original-ballistic-projectile",
                         "name=" + projectile.name + " id=" + id + " muzzleVelocity=" + velocity +
                             " travelled=" + distance +
                             " originError=" + Vector3.Distance(origin, shotMuzzlePosition));
                }
                if (observed.Count < expectedProjectiles)
                    yield return null;
            }
            if (observed.Count != expectedProjectiles)
                throw new Exception(
                    "Original projectile firing/trajectory proof failed; observed=" +
                    observed.Count + " expected=" + expectedProjectiles);
        }
        // Capture the actual loaded firearm while still held in the range,
        // after trigger acceptance; release is verified immediately afterwards.
        var gameplayCapture = Capture(renderer, target);
        while (gameplayCapture.MoveNext())
            yield return gameplayCapture.Current;
        trigger = 0;
        grip = 0;
        proximityHold = false;
        float releaseDeadline = Time.realtimeSinceStartup + 5;
        while ((hand.CurrentInteractable == gun || gun.IsHeld || gun.m_hand == hand ||
                hand.Input.IsGrabbing) &&
               Time.realtimeSinceStartup < releaseDeadline)
            yield return null;
        if (hand.CurrentInteractable == gun || gun.IsHeld || gun.m_hand == hand ||
            hand.Input.IsGrabbing)
            throw new Exception("Original open-hand input did not release firearm");
        if (fire && shots != 1)
            throw new Exception("Extra original shot events after trigger release: " + shots);
        Mark("original-open-hand-release");
        string vaultDirectory =
            Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_VAULT_DIRECTORY");
        if (!string.IsNullOrEmpty(vaultDirectory)) {
            Mark("original-vault-roundtrip", "item=" + item);
            var vault = NativeGameplayVaultFixture.RunAfterOpenHandRelease(gun, vaultDirectory);
            while (vault.MoveNext())
                yield return vault.Current;
        }
        Mark("passed", "scene=" + target + " item=" + item + " originalShotEvents=" + shots +
                           " synthetic=true");
        Debug.Log("NATIVE_GAMEPLAY_PROBE_PASS original scene transition, Anvil bundle prefab, " +
                  "collision hover, grip interaction, proximity hold, trigger input, open-hand " +
                  "release and persistent camera handoff; simulated controllers only; shots=" +
                  shots + " ballisticProjectiles=" + expectedProjectiles);
        finished = true;
        Cleanup();
        Application.Quit(0);
    }
    IEnumerator Capture(NativeStereoRenderer renderer, string target) {
        var screenshot = Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_SCREENSHOT");
        if (!string.IsNullOrEmpty(screenshot)) {
            ulong captureAfter = renderer.completedFrames + 2;
            while (renderer.completedFrames < captureAfter)
                yield return null;
            if (renderer.sourceCamera == null ||
                renderer.sourceCamera.gameObject.scene.name != target ||
                renderer.suspendSceneRendering)
                throw new Exception("Gameplay capture lost the active range camera");
            if (File.Exists(screenshot))
                File.Delete(screenshot);
            ScreenCapture.CaptureScreenshot(screenshot);
            float captureDeadline = Time.realtimeSinceStartup + 5;
            while ((!File.Exists(screenshot) || new FileInfo(screenshot).Length == 0) &&
                   Time.realtimeSinceStartup < captureDeadline)
                yield return null;
            if (!File.Exists(screenshot) || new FileInfo(screenshot).Length == 0)
                throw new Exception("Fresh gameplay screenshot was not written: " + screenshot);
            Mark("gameplay-capture", "scene=" + SceneManager.GetActiveScene().name +
                                         " completedFrames=" + renderer.completedFrames +
                                         " path=" + screenshot);
        }
    }
    static FieldInfo ProjectileField(string name, Type expected) {
        var field = typeof(BallisticProjectile)
                        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != expected)
            throw new Exception("Original ballistic schema changed: " + name);
        return field;
    }
    void Cleanup() {
        NativeDiagnosticInput.End();
        if (shotScene != null && shotListener != null)
            shotScene.ShotFiredEvent -= shotListener;
    }
    void Fail(string reason) {
        if (finished)
            return;
        finished = true;
        Debug.LogError("NATIVE_GAMEPLAY_PROBE_FAIL stage=" + stage + " " + reason);
        Cleanup();
        Application.Quit(1);
    }
    void OnDestroy() { Cleanup(); }
}

// Counts real callbacks without replacing the camera's image-effect chain.
public sealed class NativeGameplayCameraWitness : MonoBehaviour {
    public int preCull, postRender;
    void OnPreCull() { preCull++; }
    void OnPostRender() { postRender++; }
}
