using UnityEngine;
using UnityEngine.UI;
using System;
using System.IO;
using System.Collections;
public sealed class UIProbe : MonoBehaviour {
    string errors = "";
    void Awake() {
        Application.runInBackground = true;
        var camera = new GameObject("ProbeCamera").AddComponent<Camera>();
        camera.backgroundColor = Color.black;
        camera.clearFlags = CameraClearFlags.SolidColor;
        Application.logMessageReceived += (message, stack, type) => {
            if (type == LogType.Exception || type == LogType.Error)
                errors += message + "\n";
        };
    }
    IEnumerator Start() {
        var text = FindObjectOfType<Text>();
        string[] cases = { "",
                           " ",
                           "Native H3VR UI",
                           "a\nb",
                           "<b>Rich</b> <i>text</i>",
                           "line\n",
                           new string('X', 200) };
        foreach (var value in cases) {
            text.text = value;
            Canvas.ForceUpdateCanvases();
            yield return null;
            Canvas.ForceUpdateCanvases();
        }
        text.text = "Native H3VR UI";
        yield return new WaitForEndOfFrame();
        int vertexCount = text.cachedTextGenerator.vertexCount;
        var px = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        px.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        px.Apply();
        int lit = 0;
        foreach (var c in px.GetPixels(10, Screen.height / 2 - 40, Screen.width - 20, 80))
            if (c.r > 0.25f && c.g > 0.25f && c.b > 0.25f)
                lit++;
        string dir = Environment.GetEnvironmentVariable("NATIVEH3VR_UI_RESULT");
        File.WriteAllBytes(Path.Combine(dir, "text.png"), px.EncodeToPNG());
        var result = "{\"cases\":" + cases.Length + ",\"vertices\":" + vertexCount +
                     ",\"litPixels\":" + lit +
                     ",\"serializedText\":" + (text != null ? "true" : "false") +
                     ",\"exceptionCount\":" +
                     (string.IsNullOrEmpty(errors) ? 0 : errors.Split('\n').Length) + "}";
        File.WriteAllText(Path.Combine(dir, "result.json"), result);
        File.WriteAllText(Path.Combine(dir, "errors.txt"), errors);
        Debug.Log("UI_PROBE " + result);
        Application.Quit(string.IsNullOrEmpty(errors) && vertexCount > 0 && lit > 20 ? 0 : 1);
    }
}
