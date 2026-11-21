#if NATIVEH3VR_GRAPHICS_UPGRADE
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace H3VRConverter
{
    internal sealed class ThermalPackageVariants : IPreprocessShaders
    {
        private const string Package = "Packages/com.unity.render-pipelines.universal/Shaders/";
        private const string Generated = "Assets/NativeH3VR/GraphicsUpgrade/Generated/ThermalInput_";
        private const string Prefix = "Hidden/NativeH3VR/ThermalInput/Universal Render Pipeline/";
        private static readonly bool Verified = Array.IndexOf(Environment.GetCommandLineArgs(),
            "-h3vrVerifiedStockThermalVariants") >= 0;

        public int callbackOrder => 1;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!Verified || data.Count == 0 || shader == null) return;
            string source = shader.name switch
            {
                Prefix + "Lit" => Package + "Lit.shader",
                Prefix + "Simple Lit" => Package + "SimpleLit.shader",
                _ => null
            };
            if (source == null || AssetDatabase.GetAssetPath(shader) != Generated + Hash128.Compute(source) + ".shader")
                return;
            var directional = new ShaderKeyword("DIRLIGHTMAP_COMBINED");
            var realtime = new ShaderKeyword("DYNAMICLIGHTMAP_ON");

            bool Remove(ShaderCompilerData row) =>
                (row.shaderCompilerPlatform is ShaderCompilerPlatform.Metal or ShaderCompilerPlatform.D3D
                    or ShaderCompilerPlatform.Vulkan) &&
                (row.shaderKeywordSet.IsEnabled(directional) || row.shaderKeywordSet.IsEnabled(realtime));

            // Keep any unexpected pass whose domain has no known eligible row.
            bool knownRow = false;
            foreach (var row in data)
                if (!Remove(row)) { knownRow = true; break; }
            if (!knownRow) return;
            int before = data.Count;
            for (int index = data.Count - 1; index >= 0; --index)
                if (Remove(data[index])) data.RemoveAt(index);
            if (data.Count != before)
                Debug.Log($"NATIVEH3VR_THERMAL_PACKAGE_VARIANTS shader={shader.name} pass={snippet.passName} stage={snippet.shaderType} before={before} after={data.Count}");
        }
    }
}
#endif
