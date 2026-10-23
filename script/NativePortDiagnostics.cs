using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class NativePortDiagnostics : MonoBehaviour {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Begin() {
        Debug.Log("NATIVEH3VR_RUNTIME Unity=" + Application.unityVersion +
                  " Graphics=" + SystemInfo.graphicsDeviceType +
                  " GPU=" + SystemInfo.graphicsDeviceName + " OS=" + SystemInfo.operatingSystem);
        Debug.Log("NATIVEH3VR_RUN_ID " + Environment.GetEnvironmentVariable("NATIVEH3VR_RUN_ID") +
                  " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        var go = new GameObject("NativeH3VR diagnostics");
        DontDestroyOnLoad(go);
        go.AddComponent<NativePortDiagnostics>();
    }
    IEnumerator Start() {
        float seconds;
        if (!float.TryParse(Environment.GetEnvironmentVariable("NATIVEH3VR_TEST_DURATION"),
                            out seconds) ||
            seconds <= 0)
            yield break;
        yield return new WaitForSecondsRealtime(seconds);
        var scene = SceneManager.GetActiveScene();
        Debug.Log("NATIVEH3VR_OBSERVATION_END scene=" + scene.name + " roots=" + scene.rootCount +
                  " frames=" + Time.frameCount);
        var screenshot = Environment.GetEnvironmentVariable("NATIVEH3VR_SCREENSHOT");
        if (!string.IsNullOrEmpty(screenshot)) {
            ScreenCapture.CaptureScreenshot(screenshot);
            yield return new WaitForSecondsRealtime(1);
        }
        Application.Quit();
    }
}
