using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
// Diagnostic pixel comparison, not a claim that public historical source is binary-identical.
public static class FinalPassReferenceAudit {
    [Serializable]
    public class Row {
        public string keywords;
        public int pass, pattern, subshader;
        public float maxAbs, rmse, maxRGB, maxAlpha;
        public int differentPixels;
    }
    [Serializable]
    public class Report {
        public string reference, api;
        public Row[] rows;
        public string[] errors;
    }
    public static void Run() {
        ShaderUtil.allowAsyncCompilation = false;
        var errors = new List<string>();
        Application.logMessageReceivedThreaded += (m, s, t) => {
            if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert)
                lock (errors) errors.Add(m);
        };
        var json = JsonUtility.FromJson<CriticalGpuProbe.Manifest>(
            File.ReadAllText("Assets/Resources/OriginalVariants.json"));
        var rows = new List<Row>();
        var target = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat,
                                       RenderTextureReadWrite.Linear);
        target.Create();
        var read = new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true);
        var input = new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true);
        input.filterMode = FilterMode.Bilinear;
        input.wrapMode = TextureWrapMode.Clamp;
        var noise = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
        noise.SetPixel(0, 0, new Color(.5f, .5f, .5f, .5f));
        noise.Apply();
        var quad = new Mesh();
        quad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0),
                                new Vector3(-1, 1, 0) };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        Func<Material, int, Color[]> draw = (m, p) => {
            m.SetTexture("_MainTex", input);
            m.SetVector("_MainTex_TexelSize", new Vector4(1f / 32, 1f / 32, 32, 32));
            m.SetTexture("_DitheringTex", noise);
            m.SetVector("_Dithering_Coords", new Vector4(1, 1, 0, 0));
            var cmd = new CommandBuffer();
            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(false, true, Color.magenta);
            cmd.DrawMesh(quad, Matrix4x4.identity, m, 0, p);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.active = target;
            read.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
            read.Apply();
            return read.GetPixels();
        };
        var reference = new Material(Shader.Find("NativeReference/FinalPass"));
        for (int pattern = 0; pattern < 3; pattern++) {
            var c = new Color[1024];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++) {
                    float r = pattern == 0 ? (x + y > 31 ? .9f : .1f)
                                           : ((x / 3 + y / 5) % 2 == 0 ? .8f : .2f);
                    float g = pattern == 1 ? (x > 15 ? .9f : .1f) : y / 31f;
                    float b = (x * 7 + y * 3) % 31 / 31f;
                    c[y * 32 + x] = new Color(r, g, b, .2126f * r + .7152f * g + .0722f * b);
                }
            input.SetPixels(c);
            input.Apply();
            foreach (var original in json.shaders.Where(s => s.file.Contains("FinalPass__Sub"))) {
                var mat = new Material(Resources.Load<Shader>(
                    "Shaders/" + Path.GetFileNameWithoutExtension(original.file)));
                int sub = original.file.Contains("Sub1") ? 1 : 0;
                foreach (var pass in original.passes)
                    foreach (var key in pass.allowed.Where(
                                 k => !k.Split(' ').Contains("UNITY_SINGLE_PASS_STEREO"))) {
                        mat.shaderKeywords = reference.shaderKeywords =
                            key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var a = draw(mat, pass.index);
                        var b = draw(reference, pass.index);
                        double squares = 0;
                        float max = 0, maxRGB = 0, maxAlpha = 0;
                        int different = 0;
                        for (int i = 0; i < a.Length; i++) {
                            float d = 0;
                            for (int channel = 0; channel < 4; channel++) {
                                float e = Mathf.Abs(a[i][channel] - b[i][channel]);
                                max = Mathf.Max(max, e);
                                if (channel == 3)
                                    maxAlpha = Mathf.Max(maxAlpha, e);
                                else
                                    maxRGB = Mathf.Max(maxRGB, e);
                                squares += e * e;
                                d = Mathf.Max(d, e);
                            }
                            if (d > 1f / 255)
                                different++;
                        }
                        rows.Add(new Row { keywords = key, pass = pass.index, subshader = sub,
                                           pattern = pattern, maxAbs = max, maxRGB = maxRGB,
                                           maxAlpha = maxAlpha,
                                           rmse = (float)Math.Sqrt(squares / (a.Length * 4)),
                                           differentPixels = different });
                    }
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }
        var report = new Report { reference = "Historical Unity PostProcessing source; " +
                                              "structural/ABI match, not proven bytecode-identical",
                                  api = SystemInfo.graphicsDeviceType.ToString(),
                                  rows = rows.ToArray(), errors = errors.ToArray() };
        File.WriteAllText(Path.GetFullPath("../shaders/finalpass-reference-pixel-comparison.json"),
                          JsonUtility.ToJson(report, true));
        Debug.Log("FINALPASS_REFERENCE rows=" + rows.Count + " max=" + rows.Max(r => r.maxAbs));
        EditorApplication.Exit(errors.Count == 0 ? 0 : 1);
    }
}
