using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
public static class ComputeAudit2021 {
    [Serializable]
    public class Kernel {
        public string name;
        public uint x, y, z;
        public string error;
    }
    [Serializable]
    public class Entry {
        public string path;
        public Kernel[] kernels;
    }
    [Serializable]
    public class Report {
        public string api, device;
        public Entry[] shaders;
    }
    public static void Run() {
        var entries = new List<Entry>();
        foreach (var guid in AssetDatabase.FindAssets("t:ComputeShader",
                                                      new[] { "Assets/Resources/Computes" })) {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            var s = AssetDatabase.LoadAssetAtPath<ComputeShader>(p);
            var kernels = new List<Kernel>();
            foreach (Match m in Regex.Matches(File.ReadAllText(p), @"#pragma kernel (\w+)")) {
                var k = new Kernel { name = m.Groups[1].Value };
                try {
                    s.GetKernelThreadGroupSizes(s.FindKernel(k.name), out k.x, out k.y, out k.z);
                } catch (Exception e) {
                    k.error = e.ToString();
                }
                kernels.Add(k);
            }
            entries.Add(new Entry { path = p, kernels = kernels.ToArray() });
        }
        File.WriteAllText(
            Path.GetFullPath("../shaders/compute-2021-compile-report.json"),
            JsonUtility.ToJson(new Report { api = SystemInfo.graphicsDeviceType.ToString(),
                                            device = SystemInfo.graphicsDeviceName,
                                            shaders = entries.ToArray() },
                               true));
        EditorApplication.Exit(
            entries.Exists(e => Array.Exists(e.kernels, k => k.error != null)) ? 1 : 0);
    }
}
