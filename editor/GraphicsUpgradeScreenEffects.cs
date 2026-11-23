using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace H3VRConverter
{
    public static class GraphicsUpgradeScreenEffects
    {
        private const string ShaderPath =
            "Assets/NativeH3VR/GraphicsUpgrade/Shaders/Resources/NativeH3VRScreenEffects.shader";
        private const string BundleName = "nativeh3vr-urp-screen-effects";

        public static void BuildShaderBundle()
        {
            if (GraphicsSettings.defaultRenderPipeline is not UniversalRenderPipelineAsset ||
                PlayerSettings.colorSpace != ColorSpace.Linear)
                throw new InvalidOperationException("Build the screen effects bundle in a converted linear URP project.");

            string[] arguments = Environment.GetCommandLineArgs();
            int report = Array.IndexOf(arguments, "-h3vrReport");
            if (report < 0 || report + 1 == arguments.Length)
                throw new ArgumentException("Supply a private output directory as -h3vrReport.");
            string output = Path.GetFullPath(arguments[report + 1]);
            Directory.CreateDirectory(output);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (!shader || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("Could not import the NativeH3VR screen effects shader.");
            var request = new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { ShaderPath } };
            if (!BuildPipeline.BuildAssetBundles(output, new[] { request }, BuildAssetBundleOptions.ChunkBasedCompression,
                    BuildTarget.StandaloneOSX))
                throw new InvalidOperationException("Metal screen effects bundle build failed.");
            if (ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("The Metal screen effects shader has a compiler error.");
        }
    }
}
