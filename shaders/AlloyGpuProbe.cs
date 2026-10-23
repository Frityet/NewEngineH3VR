using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
public class AlloyGpuProbe : MonoBehaviour {
    [Serializable]
    public class Draw {
        public string material, keywords;
        public int pass;
        public bool supported;
    }
    [Serializable]
    public class Report {
        public string api, device;
        public bool passed;
        public int materials, draws;
        public float flatBrightness, bumpBrightness, normalMapDifference;
        public Draw[] variants;
        public string[] errors;
    }
    readonly List<string> errors = new List<string>();
    void Awake() {
        Application.logMessageReceivedThreaded += (m, s, t) => {
            if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert)
                lock (errors) errors.Add(m);
        };
    }
    IEnumerator Start() {
        yield return null;
        yield return null;
        Run();
    }
    static Color[] Read(RenderTexture target) {
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
        pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
        pixels.Apply();
        var result = pixels.GetPixels();
        Destroy(pixels);
        RenderTexture.active = previous;
        return result;
    }
    void Run() {
        var rows = new List<Draw>();
        var mats = Resources.LoadAll<Material>("Variants");
        var target = new RenderTexture(16, 16, 24, RenderTextureFormat.ARGB32);
        target.Create();
        var quad = new Mesh();
        quad.vertices = new[] { new Vector3(-1, -1, .5f), new Vector3(1, -1, .5f),
                                new Vector3(1, 1, .5f), new Vector3(-1, 1, .5f) };
        quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        quad.tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1),
                                new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        foreach (var mat in mats)
            for (int pass = 0; pass < mat.passCount; pass++) {
                var cmd = new CommandBuffer();
                cmd.SetRenderTarget(target);
                cmd.ClearRenderTarget(true, true, Color.gray);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.DrawMesh(quad, Matrix4x4.identity, mat, 0, pass);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();
                Read(target);
                rows.Add(new Draw { material = mat.name,
                                    keywords = string.Join(" ", mat.shaderKeywords), pass = pass,
                                    supported = mat.shader.isSupported });
            }
        target.Release();
        Destroy(target);
        var material = new Material(Resources.Load<Shader>("Shaders/Alloy_Core"));
        material.SetFloat("_SrcBlend", 1);
        material.SetFloat("_DstBlend", 0);
        material.SetFloat("_ZWrite", 1);
        material.SetColor("_Color", Color.white);
        material.SetFloat("_Metal", 0);
        material.SetFloat("_Occlusion", 0);
        material.SetFloat("_BumpScale", 1);
        material.SetFloat("_Roughness", 1);
        material.SetFloat("_Specularity", 0);
        material.SetTexture("_MainTex", Texture2D.whiteTexture);
        var normal = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        normal.SetPixels(new[] { new Color(.95f, .5f, 1, 1), new Color(.95f, .5f, 1, 1),
                                 new Color(.95f, .5f, 1, 1), new Color(.95f, .5f, 1, 1) });
        normal.Apply();
        material.SetTexture("_BumpMap", normal);
        var surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
        surface.GetComponent<Renderer>().sharedMaterial = material;
        surface.transform.localScale = Vector3.one * 2;
        var camera = new GameObject("Feature camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -3);
        camera.orthographic = true;
        camera.orthographicSize = 1;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.renderingPath = RenderingPath.Forward;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.targetTexture = new RenderTexture(32, 32, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture.Create();
        var light = new GameObject("Feature light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 1;
        light.transform.rotation = Quaternion.identity;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        material.DisableKeyword("EFFECT_BUMP");
        camera.Render();
        var flat = Read(camera.targetTexture);
        material.EnableKeyword("EFFECT_BUMP");
        camera.Render();
        var bump = Read(camera.targetTexture);
        float a = 0, b = 0, d = 0;
        for (int i = 0; i < flat.Length; i++) {
            a += flat[i].grayscale;
            b += bump[i].grayscale;
            d += Mathf.Abs(flat[i].r - bump[i].r) + Mathf.Abs(flat[i].g - bump[i].g) +
                 Mathf.Abs(flat[i].b - bump[i].b);
        }
        a /= flat.Length;
        b /= bump.Length;
        d /= flat.Length * 3;
        var report = new Report { api = SystemInfo.graphicsDeviceType.ToString(),
                                  device = SystemInfo.graphicsDeviceName,
                                  materials = mats.Length,
                                  draws = rows.Count,
                                  variants = rows.ToArray(),
                                  flatBrightness = a,
                                  bumpBrightness = b,
                                  normalMapDifference = d };
        lock (errors) report.errors = errors.ToArray();
        report.passed = report.api == "Metal" && mats.Length >= 95 &&
                        rows.TrueForAll(x => x.supported) && report.errors.Length == 0 && d > .02f;
        File.WriteAllText(Environment.GetEnvironmentVariable("NATIVEH3VR_ALLOY_REPORT"),
                          JsonUtility.ToJson(report, true));
        Debug.Log("ALLOY_GPU_PROBE " + report.passed + " materials=" + mats.Length +
                  " draws=" + rows.Count + " normalDifference=" + d);
        Application.Quit(report.passed ? 0 : 1);
    }
}
