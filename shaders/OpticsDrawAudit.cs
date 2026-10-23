using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Force a GPU submission/readback for each original pass-specific keyword set.
// Import and Material.SetPass alone missed invalid Metal stage interfaces previously.
public static class OpticsDrawAudit {
    [Serializable]
    public class Entry {
        public string keywords;
        public int pass;
        public bool supported;
        public string[] errors;
    }
    [Serializable]
    public class Report {
        public string api, path;
        public bool complete, passed;
        public int expected;
        public Entry[] variants;
        public string[] runtimeErrors;
    }
    public static void Run() {
        foreach (var path in new[] { "Assets/Resources/Shaders/Unlit_ScopeClearLens_0.shader",
                                     "Assets/Resources/Shaders/Hidden_PIPScope_PostProcess.shader",
                                     "Assets/Resources/Shaders/Reflex Sight_Reflex Sight " +
                                         "Occluded (Fiber Optic).shader",
                                     "Assets/Resources/Shaders/Reflex Sight_Reflex Sight_0.shader",
                                     "Assets/Resources/Shaders/Reflex Sight_Reflex Sight.shader",
                                     "Assets/Resources/Shaders/Unlit_ScopeClearLens.shader" })
            RunOne(path);
        EditorApplication.Exit(0);
    }
    static void RunOne(string path) {
        ShaderUtil.allowAsyncCompilation = false;
        string reportPath =
            Path.GetFullPath("../shaders/optics-pending/" + Path.GetFileNameWithoutExtension(path) +
                             "-draw-report.json");
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
        if (shader == null)
            throw new InvalidOperationException("PIP source did not import");
        var rows = new List<Entry>();
        var runtimeErrors = new List<string>();
        Application.LogCallback capture = (message, stack, type) => {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                lock (runtimeErrors) runtimeErrors.Add(message);
        };
        Application.logMessageReceivedThreaded += capture;
        var quad = new Mesh();
        quad.vertices = new[] { new Vector3(-1, -1, .5f), new Vector3(1, -1, .5f),
                                new Vector3(1, 1, .5f), new Vector3(-1, 1, .5f) };
        quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        quad.tangents = new[] { Vector4.one, Vector4.one, Vector4.one, Vector4.one };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        var target = new RenderTexture(8, 8, 24);
        target.Create();
        var pixels = new Texture2D(1, 1);
        var report = new Report {
            api = SystemInfo.graphicsDeviceType.ToString(), path = path,
            expected =
                Regex.Matches(File.ReadAllText(path), @"^\s*#(?:if|elif) ", RegexOptions.Multiline)
                    .Count
        };
        Action<bool> save = (complete) => {
            report.complete = complete;
            report.variants = rows.ToArray();
            lock (runtimeErrors) report.runtimeErrors = runtimeErrors.ToArray();
            report.passed = complete && rows.Count == report.expected &&
                            report.runtimeErrors.Length == 0 &&
                            rows.TrueForAll(r => r.supported && r.errors.Length == 0);
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        };
        int pass = 0;
        foreach (Match program in Regex.Matches(File.ReadAllText(path), @"CGPROGRAM(.*?)ENDCG",
                                                RegexOptions.Singleline)) {
            var keys = new HashSet<string>();
            foreach (Match m in Regex.Matches(program.Groups[1].Value, @"^\s*#(?:if|elif) (.+)$",
                                              RegexOptions.Multiline)) {
                var names = new List<string>();
                foreach (Match k in Regex.Matches(m.Groups[1].Value, @"(?<!!)defined\((\w+)\)"))
                    names.Add(k.Groups[1].Value);
                names.Sort();
                keys.Add(string.Join(" ", names));
            }
            foreach (var key in keys) {
                var mat = new Material(shader);
                mat.shaderKeywords = key.Length == 0 ? new string[0] : key.Split(' ');
                var errors = new List<string>();
                try {
                    var cmd = new CommandBuffer();
                    cmd.SetRenderTarget(target);
                    cmd.ClearRenderTarget(true, true, Color.gray);
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    cmd.DrawMesh(quad, Matrix4x4.identity, mat, 0, pass);
                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Release();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(4, 4, 1, 1), 0, 0);
                    pixels.Apply();
                } catch (Exception e) {
                    errors.Add(e.ToString());
                }
                foreach (var m in ShaderUtil.GetShaderMessages(shader))
                    if (m.severity.ToString() == "Error")
                        errors.Add(m.message);
                rows.Add(new Entry { keywords = key, pass = pass, supported = shader.isSupported,
                                     errors = errors.ToArray() });
                UnityEngine.Object.DestroyImmediate(mat);
                if (rows.Count % 32 == 0) {
                    save(false);
                    Debug.Log("OPTICS_VARIANT_DRAW " + Path.GetFileName(path) + " " + rows.Count +
                              "/" + report.expected);
                }
            }
            pass++;
        }
        RenderTexture.active = null;
        save(true);
        Application.logMessageReceivedThreaded -= capture;
        UnityEngine.Object.DestroyImmediate(pixels);
        target.Release();
        UnityEngine.Object.DestroyImmediate(target);
        UnityEngine.Object.DestroyImmediate(quad);
        if (!report.passed)
            throw new InvalidOperationException("Variant draw failed: " + path);
    }
}
