using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
public static class HistoricalShadowAudit {
    [Serializable]
    public class Row {
        public string shader, keywords;
        public int pass;
        public bool supported;
        public string[] errors;
    }
    [Serializable]
    public class Report {
        public string api;
        public bool passed;
        public Row[] draws;
        public string[] errors;
    }
    public static void Run() {
        ShaderUtil.allowAsyncCompilation = false;
        var rows = new List<Row>();
        var errors = new HashSet<string>();
        Application.logMessageReceivedThreaded += (m, s, t) => {
            if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert)
                lock (errors) errors.Add(m);
        };
        var quad = new Mesh();
        quad.vertices = new[] { new Vector3(-1, -1, .5f), new Vector3(1, -1, .5f),
                                new Vector3(1, 1, .5f), new Vector3(-1, 1, .5f) };
        quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        quad.uv2 = quad.uv;
        quad.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
        quad.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 4).ToArray();
        quad.colors = Enumerable.Repeat(Color.white, 4).ToArray();
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        var target = new RenderTexture(8, 8, 24);
        target.Create();
        var read = new Texture2D(1, 1);
        foreach (string file in new[] { "Alloy_Car Paint", "Alloy_Vertex Blend",
                                        "Alloy_Directional Blend", "Alloy_TriPlanar" }) {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/Shaders/" + file +
                                                               ".shader");
            var material = new Material(shader);
            material.SetFloat("_SrcBlend", 1);
            material.SetFloat("_DstBlend", 0);
            material.SetFloat("_ZWrite", 1);
            var materials = File.ReadAllLines("Assets/Resources/AlloyCompatKeywords.txt")
                                .Where(l => l.StartsWith(file + "|"))
                                .Select(l => l.Split('|')[1])
                                .Concat(new[] { "" })
                                .Distinct();
            foreach (string materialKeys in materials)
                foreach (string lighting in new[] {
                             "DIRECTIONAL LIGHTPROBE_SH SHADOWS_SCREEN SHADOWS_SHADOWMASK",
                             "DIRECTIONAL SHADOWS_SCREEN SHADOWS_SHADOWMASK",
                             "DIRECTIONAL LIGHTPROBE_SH SHADOWS_SCREEN",
                             "DIRECTIONAL LIGHTMAP_ON DIRLIGHTMAP_COMBINED SHADOWS_SCREEN " +
                                 "SHADOWS_SHADOWMASK",
                             "DIRECTIONAL LIGHTMAP_ON LIGHTMAP_SHADOW_MIXING SHADOWS_SCREEN " +
                                 "SHADOWS_SHADOWMASK"
                         })
                    foreach (string fog in new[] { "", "FOG_EXP", "FOG_EXP2" }) {
                        string key = (materialKeys + " " + lighting + " " + fog).Trim();
                        material.shaderKeywords =
                            key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                .Distinct()
                                .ToArray();
                        int pass = material.FindPass("FORWARD");
                        var cmd = new CommandBuffer();
                        cmd.SetRenderTarget(target);
                        cmd.ClearRenderTarget(true, true, Color.gray);
                        cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                        cmd.DrawMesh(quad, Matrix4x4.identity, material, 0, pass);
                        Graphics.ExecuteCommandBuffer(cmd);
                        cmd.Release();
                        RenderTexture.active = target;
                        read.ReadPixels(new Rect(4, 4, 1, 1), 0, 0);
                        read.Apply();
                        var messages = ShaderUtil.GetShaderMessages(shader)
                                           .Where(m => m.severity.ToString() == "Error")
                                           .Select(m => m.message)
                                           .ToArray();
                        rows.Add(new Row { shader = file, keywords = key, pass = pass,
                                           supported = shader.isSupported, errors = messages });
                        if (messages.Length > 0 || errors.Count > 0)
                            break;
                    }
            UnityEngine.Object.DestroyImmediate(material);
        }
        var report = new Report { api = SystemInfo.graphicsDeviceType.ToString(),
                                  draws = rows.ToArray(), errors = errors.ToArray() };
        report.passed = report.api == "Metal" && report.errors.Length == 0 &&
                        rows.TrueForAll(r => r.supported && r.errors.Length == 0);
        File.WriteAllText(
            Path.GetFullPath("../shaders/alloy-historical-shadowmask-draw-report.json"),
            JsonUtility.ToJson(report, true));
        Debug.Log("HISTORICAL_SHADOWMASK_DRAW " + report.passed + " draws=" + rows.Count);
        EditorApplication.Exit(report.passed ? 0 : 1);
    }
}
