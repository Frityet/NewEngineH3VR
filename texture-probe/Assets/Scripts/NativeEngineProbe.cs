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
        var t = GameObject.Find("Cube").GetComponent<Renderer>().sharedMaterial.mainTexture
                    as Texture2D;
        Debug.Log("NATIVE_TEXTURE_GPU format=" + t.format + " ptr=" + t.GetNativeTexturePtr());
        var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32,
                                            RenderTextureReadWrite.Linear);
        Graphics.Blit(t, rt);
        RenderTexture.active = rt;
        var capture = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, true);
        capture.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
        capture.Apply();
        File.WriteAllBytes(Environment.GetEnvironmentVariable("NATIVEH3VR_TEXTURE_CAPTURE"),
                           ImageConversion.EncodeToPNG(capture));
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
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
