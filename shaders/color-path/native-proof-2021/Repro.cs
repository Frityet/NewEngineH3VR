using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
public static class Repro {
    static readonly string[] ShaderPaths = {
        "Assets/Shader/Alloy_Core (Camera Relative).shader",
        "Assets/Shader/Alloy_Core (Camera Relative)_0.shader",
        "Assets/Shader/Alloy_Core (Camera Relative)_1.shader",
        "Assets/Shader/CynCliff HeightBlend.shader",
        "Assets/Shader/NatureManufacture Shaders_Cross Model Shader.shader",
        "Assets/Shader/NatureManufacture Shaders_Tree Bark Metalic.shader",
        "Assets/Shader/NatureManufacture Shaders_Tree_Leaves_Specular.shader",
        "Assets/Shader/Toon_Lit.shader",
        "Assets/Shader/VTP_Glass_2 Texture blend (2-sided).shader",
        "Assets/Shader/VTP_Metallic_3 Texture Blend (Advanced).shader",
        "Assets/Shader/VTP_Metallic_3 Texture Blend (Advanced_ Wetness_ NoFlow).shader",
        "Assets/alloy/shaders/sm3/core/prototyping.shader",
    };
    static void DumpShaderMessages(string phase, string path, Shader shader) {
        var messages = ShaderUtil.GetShaderMessages(shader);
        Debug.Log("SHADER_MESSAGES " + phase + " path=" + path + " count=" + messages.Length);
        foreach (var item in messages)
            Debug.Log("SHADER_MESSAGE " + phase + " path=" + path + " " + item.severity + " " +
                      item.message + " line=" + item.line);
    }
    public static void Run() {
        try {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                           new[] { GraphicsDeviceType.Metal });
            int i = 0;
            foreach (var path in ShaderPaths) {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null)
                    throw new Exception("Cannot load shader " + path);
                var materialPath = "Assets/Probe/material-" + i + ".mat";
                var old = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (old != null)
                    AssetDatabase.DeleteAsset(materialPath);
                AssetDatabase.CreateAsset(new Material(shader), materialPath);
                AssetImporter.GetAtPath(materialPath).SetAssetBundleNameAndVariant("probe", "");
                i++;
            }
            AssetDatabase.SaveAssets();
            for (i = 0; i < ShaderPaths.Length; i++)
                DumpShaderMessages("before-configure", ShaderPaths[i],
                                   AssetDatabase.LoadAssetAtPath<Shader>(ShaderPaths[i]));
            NativeOriginalVariantFilter.Configure();
            for (i = 0; i < ShaderPaths.Length; i++)
                DumpShaderMessages("after-configure", ShaderPaths[i],
                                   AssetDatabase.LoadAssetAtPath<Shader>(ShaderPaths[i]));
            foreach (var path in ShaderPaths)
                ShaderUtil.ClearShaderMessages(AssetDatabase.LoadAssetAtPath<Shader>(path));
            for (i = 0; i < ShaderPaths.Length; i++)
                DumpShaderMessages("after-clear", ShaderPaths[i],
                                   AssetDatabase.LoadAssetAtPath<Shader>(ShaderPaths[i]));
            var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../BuildOutput"));
            Directory.CreateDirectory(outPath);
            var manifest = BuildPipeline.BuildAssetBundles(
                outPath,
                BuildAssetBundleOptions.ChunkBasedCompression |
                    BuildAssetBundleOptions.ForceRebuildAssetBundle |
                    BuildAssetBundleOptions.StrictMode,
                BuildTarget.StandaloneOSX);
            for (i = 0; i < ShaderPaths.Length; i++)
                DumpShaderMessages("after-build", ShaderPaths[i],
                                   AssetDatabase.LoadAssetAtPath<Shader>(ShaderPaths[i]));
            Debug.Log("REPRO_RESULT manifest=" + (manifest != null) + " output=" + outPath);
            EditorApplication.Exit(manifest == null ? 3 : 0);
        } catch (Exception e) {
            Debug.LogException(e);
            EditorApplication.Exit(4);
        }
    }
}
