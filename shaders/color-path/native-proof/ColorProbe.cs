using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
public class ColorProbe : MonoBehaviour {
    [Serializable]
    public class Row {
        public int branch;
        public string kind;
        public Color input, actual, expected;
        public float error;
    }
    [Serializable]
    public class Report {
        public bool passed;
        public string api, device;
        public int positives, negativeDetected, swizzleOnlyDetected;
        public float maxError;
        public Row[] rows;
        public string[] errors;
    }
    List<string> errors = new List<string>();
    void Awake() {
        Application.logMessageReceivedThreaded += (m, s, t) => {
            if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert)
                lock (errors) errors.Add(m);
        };
    }
    IEnumerator Start() {
        yield return null;
        Run();
    }
    Texture2D Tex(Color c) {
        var t = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
    float Log(float x) {
        return Mathf.Clamp01(Mathf.Log(x * 5.555556f + .047996f, 2) * .0734998f + .386036f);
    }
    void Run() {
        var rows = new List<Row>();
        var rt = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat,
                                   RenderTextureReadWrite.Linear);
        rt.Create();
        var read = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
        var lut = new Texture3D(33, 33, 33, TextureFormat.RGBAFloat, false);
        lut.wrapMode = TextureWrapMode.Clamp;
        lut.filterMode = FilterMode.Bilinear;
        var cols = new Color[33 * 33 * 33];
        for (int z = 0; z < 33; z++)
            for (int y = 0; y < 33; y++)
                for (int x = 0; x < 33; x++)
                    cols[x + 33 * (y + 33 * z)] = new Color(x / 32f, y / 32f, z / 32f, 1);
        lut.SetPixels(cols);
        lut.Apply();
        var mesh = new Mesh();
        mesh.vertices =
            new[] { new Vector3(-1, -1, 0), new Vector3(-1, 3, 0), new Vector3(3, -1, 0) };
        mesh.triangles = new[] { 0, 1, 2 };
        var inputs = new[] { new Color(0, 0, 0, 1),         new Color(.18f, .18f, .18f, 1),
                             new Color(1, 1, 1, 1),         new Color(.8f, .04f, .2f, 1),
                             new Color(.03f, .7f, .12f, 1), new Color(.1f, .02f, .9f, 1),
                             new Color(2, .2f, 4, 1),       new Color(.001f, .05f, .5f, 1) };
        int positives = 0, negative = 0, swizzleOnly = 0;
        float max = 0;
        for (int branch = 0; branch < 4; branch++)
            foreach (string kind in new[] { "Broken", "SwizzleOnly", "Corrected" }) {
                var shader = Resources.Load<Shader>(kind + branch);
                if (shader == null || !shader.isSupported) {
                    errors.Add("Unsupported " + kind + branch);
                    continue;
                }
                var m = new Material(shader);
                m.SetTexture("_AutoExposureTex", Tex(Color.white));
                m.SetTexture("_DitheringTex", Tex(new Color(.5f, .5f, .5f, .5f)));
                m.SetVector("_Dithering_Coords", new Vector4(1, 1, 0, 0));
                m.SetTexture("_Lut3D", lut);
                m.SetVector("_Lut3D_Params", new Vector4(1 / 33f, 32, 0, 0));
                m.SetFloat("_PostExposure", 1);
                m.SetTexture("_BloomTex", Tex(Color.clear));
                m.SetTexture("_Bloom_DirtTex", Tex(Color.clear));
                m.SetVector("_Bloom_Settings", Vector4.zero);
                m.SetVector("_Bloom_Color", Vector4.one);
                foreach (var input in inputs) {
                    var tex = Tex(input);
                    m.SetTexture("_MainTex", tex);
                    var cmd = new CommandBuffer();
                    cmd.SetRenderTarget(rt);
                    cmd.ClearRenderTarget(false, true, Color.magenta);
                    cmd.DrawMesh(mesh, Matrix4x4.identity, m, 0, 0);
                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Release();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                    read.Apply();
                    Color actual = read.GetPixel(4, 4);
                    Color expected = new Color(Log(input.r), Log(input.g),
                                               Mathf.Clamp01((Log(input.b) * 33f - .5f) / 32f), 1);
                    float e = Mathf.Max(Mathf.Abs(actual.r - expected.r),
                                        Mathf.Abs(actual.g - expected.g),
                                        Mathf.Abs(actual.b - expected.b));
                    rows.Add(new Row { branch = branch, kind = kind, input = input, actual = actual,
                                       expected = expected, error = e });
                    if (kind == "Corrected") {
                        positives++;
                        max = Mathf.Max(max, e);
                        if (e > .0002f || float.IsNaN(e))
                            errors.Add("Mismatch " + branch + " " + e);
                    } else if (kind == "Broken" && e > .01f)
                        negative++;
                    else if (kind == "SwizzleOnly" && e > .0002f)
                        swizzleOnly++;
                    Destroy(tex);
                }
                Destroy(m);
            }
        var report = new Report { api = SystemInfo.graphicsDeviceType.ToString(),
                                  device = SystemInfo.graphicsDeviceName,
                                  positives = positives,
                                  negativeDetected = negative,
                                  swizzleOnlyDetected = swizzleOnly,
                                  maxError = max,
                                  rows = rows.ToArray(),
                                  errors = errors.ToArray() };
        report.passed = positives == 32 && negative == 32 && swizzleOnly == 32 &&
                        errors.Count == 0 && report.api == "Metal";
        File.WriteAllText(Environment.GetEnvironmentVariable("COLOR_PROOF_REPORT"),
                          "{\"passed\":" + report.passed.ToString().ToLower() + ",\"api\":\"" +
                              report.api + "\",\"device\":\"" + report.device +
                              "\",\"positives\":" + positives +
                              ",\"negativeDetected\":" + negative +
                              ",\"swizzleOnlyDetected\":" + swizzleOnly + ",\"maxError\":" + max +
                              ",\"errorCount\":" + errors.Count + ",\"errorDetails\":\"" +
                              String.Join("|", report.errors).Replace("\"", "'") + "\"}");
        Debug.Log("COLOR_PROOF " + report.passed + " error=" + max + " negatives=" + negative);
        Application.Quit(report.passed ? 0 : 1);
    }
}
