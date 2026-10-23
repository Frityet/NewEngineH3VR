using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Copy into the intended project's Assets/Editor and call before building.
public static class ForceComputeImports {
    public static void Run() {
        foreach (var path in Directory.GetFiles("Assets/ComputeShader", "*.compute")) {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate |
                                                ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (!(importer is ComputeShaderImporter) || shader == null)
                throw new InvalidOperationException("Compute source did not import: " + path +
                                                    " importer=" + importer);
            foreach (Match match in Regex.Matches(File.ReadAllText(path),
                                                  @"#pragma kernel (\w+)")) {
                var name = match.Groups[1].Value;
                // FindKernel and group-size queries require a graphics backend.
                // Unity 2021 reports missing kernels under -nographics; Unity
                // 2020 can crash while creating the native compute pipeline.
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) {
                    int kernel = shader.FindKernel(name);
                    shader.GetKernelThreadGroupSizes(kernel, out var x, out var y, out var z);
                    Debug.Log("COMPUTE_IMPORT_OK " + path + " " + name + " " + x + "," + y + "," +
                              z);
                } else
                    Debug.Log("COMPUTE_SOURCE_IMPORTED " + path + " declaredKernel=" + name +
                              " (GPU validation requires Metal; graphics device is Null)");
            }
        }
        AssetDatabase.SaveAssets();
    }
}
