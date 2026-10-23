using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.IO;

public sealed class NativeEngineProbe : MonoBehaviour {
    private int frames;
    private string result;
    void Start() {
        result = "Native engine probe — not H3VR gameplay\n" + Application.unityVersion + " / " +
                 SystemInfo.operatingSystem + "\n" + SystemInfo.graphicsDeviceType + " / " +
                 SystemInfo.graphicsDeviceName;
        Debug.Log("NATIVEH3VR_ENGINE_START " + result.Replace('\n', ' '));
    }
    void Update() {
        if (++frames != 120)
            return;
        var path = Environment.GetEnvironmentVariable("NATIVEH3VR_PROBE_RESULT");
        if (string.IsNullOrEmpty(path))
            return;
        bool metal = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;
        File.WriteAllText(path, "{\"metal\":" + (metal ? "true" : "false") + ",\"frames\":" +
                                    frames + ",\"unity\":\"" + Application.unityVersion +
                                    "\",\"gpu\":\"" + SystemInfo.graphicsDeviceName + "\"}\n");
        Debug.Log("NATIVEH3VR_ENGINE_COMPLETE " + frames);
        Application.Quit(metal ? 0 : 1);
    }
    void OnGUI() { GUI.Label(new Rect(30, 30, Screen.width - 60, 100), result); }
}
