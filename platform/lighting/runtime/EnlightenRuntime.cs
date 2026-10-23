using System;
using UnityEngine;
using UnityEngine.Rendering;
public sealed class EnlightenRuntime : MonoBehaviour {
    int frames;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void StartProbe() {
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        new GameObject("GI runtime proof").AddComponent<EnlightenRuntime>();
    }
    void Update() {
        if (++frames == 1) {
            Debug.Log(
                "GI_RUNTIME_START cpu=" + SystemInfo.processorType +
                " gpu=" + SystemInfo.graphicsDeviceName + " probes=" +
                (LightmapSettings.lightProbes == null ? -1 : LightmapSettings.lightProbes.count));
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.red;
            DynamicGI.UpdateEnvironment();
        }
        if (frames == 90) {
            var p = LightmapSettings.lightProbes;
            if (p == null || p.count == 0) {
                Debug.LogError("GI_RUNTIME_FAIL no probes");
                Application.Quit(1);
                return;
            }
            SphericalHarmonicsL2 sh;
            LightProbes.GetInterpolatedProbe(p.positions[0], null, out sh);
            Debug.Log("GI_RUNTIME_SAMPLE frames=" + frames + " probes=" + p.count + " sample=" +
                      p.positions[0] + " sh=" + sh[0, 0] + "," + sh[1, 0] + "," + sh[2, 0]);
            Application.Quit(0);
        }
    }
}
