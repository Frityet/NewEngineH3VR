using System;
using System.Collections;
using FistVR;
using NativeH3VR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.VR;

// Opt-in convenience for the headset demo. This only uses the game's normal
// scene loader; hands, item spawning and firearm operation remain player-driven.
public sealed class NativeDemoStartup : MonoBehaviour {
    const string Scene = "IndoorRange_Updated";
    float deadline;
    bool finished;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install() {
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_HEADSET_DEMO") != "1")
            return;
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_PROBE") == "1")
            throw new InvalidOperationException(
                "Headset demo and gameplay acceptance probe cannot run together.");
        var host = new GameObject("Native H3VR headset demo startup");
        DontDestroyOnLoad(host);
        host.AddComponent<NativeDemoStartup>();
    }

    void Update() {
        if (!finished && deadline > 0 && Time.realtimeSinceStartup > deadline) {
            finished = true;
            Debug.LogError("NATIVE_HEADSET_DEMO_FAILED timed out loading " + Scene);
        }
    }

    IEnumerator Start() {
        deadline = Time.realtimeSinceStartup + 300;
        while (!finished && (SceneManager.GetActiveScene().name != "MainMenu3" ||
                             FindObjectOfType<IM>() == null || !NativeSteamVRSystem.Running ||
                             IM.OD == null || IM.OD.Count == 0 ||
                             (GM.LoadingCallback != null && !GM.LoadingCallback.IsCompleted)))
            yield return null;
        NativeStereoRenderer menuRenderer = null;
        while (!finished) {
            menuRenderer = FindObjectOfType<NativeStereoRenderer>();
            if (menuRenderer != null && menuRenderer.sourceCamera != null &&
                menuRenderer.completedFrames > 0)
                break;
            yield return null;
        }
        if (finished)
            yield break;
        if (!Application.CanStreamedLevelBeLoaded(Scene)) {
            finished = true;
            Debug.LogError("NATIVE_HEADSET_DEMO_FAILED range missing from player: " + Scene);
            yield break;
        }
        Debug.Log("NATIVE_HEADSET_DEMO_LOADING scene=" + Scene +
                  " tracking=" + Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR"));
        SteamVR_LoadLevel.Begin(Scene);
        while (!finished && (SceneManager.GetActiveScene().name != Scene ||
                             SteamVR_LoadLevel.loading || GM.CurrentSceneSettings == null))
            yield return null;
        NativeStereoRenderer renderer = null;
        while (!finished) {
            renderer = FindObjectOfType<NativeStereoRenderer>();
            if (renderer != null && renderer.sourceCamera != null &&
                renderer.sourceCamera.gameObject.scene.name == Scene &&
                !renderer.suspendSceneRendering)
                break;
            yield return null;
        }
        if (finished)
            yield break;
        ulong initialFrames = renderer.completedFrames;
        while (!finished && renderer != null && renderer.completedFrames < initialFrames + 3)
            yield return null;
        if (finished)
            yield break;
        finished = true;
        if (renderer == null)
            Debug.LogError("NATIVE_HEADSET_DEMO_FAILED stereo renderer lost");
        else
            Debug.Log("NATIVE_HEADSET_DEMO_READY scene=" + Scene +
                      " stereoFrames=" + renderer.completedFrames +
                      " tracking=" + Environment.GetEnvironmentVariable("NATIVEH3VR_OPENXR") +
                      " controls=player-driven");
    }
}
