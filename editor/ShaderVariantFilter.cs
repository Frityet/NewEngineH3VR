using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace H3VRConverter
{
    /// <summary>Strip requests excluded by a complete original pass group.</summary>
    internal sealed class ShaderVariantFilter : IPreprocessShaders
    {
        private static readonly Lazy<Dictionary<string, KnownShader>> Catalog =
            new Lazy<Dictionary<string, KnownShader>>(LoadCatalog);

        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (snippet.shaderType != ShaderType.Vertex && snippet.shaderType != ShaderType.Fragment) return;
            var mode = LightMode(snippet.passType);
            bool srp = false;
#if NATIVEH3VR_GRAPHICS_UPGRADE
            srp = snippet.passType == PassType.ScriptableRenderPipeline;
#endif
            if ((mode == null && !srp) || !data.Any(IsRecoveredPlatform)) return;

            var path = AssetDatabase.GetAssetPath(shader);
            var name = (snippet.passName ?? "").Trim().ToUpperInvariant();
            string originalPath = path;
            int subshader = (int)snippet.pass.SubshaderIndex;
#if NATIVEH3VR_GRAPHICS_UPGRADE
            if (GraphicsUpgradeThermalShaders.TryMatchBranch(path, out string recoveredPath, out int recoveredSubshader))
            {
                originalPath = recoveredPath;
                subshader = recoveredSubshader;
            }
#endif
            if (string.IsNullOrEmpty(originalPath) || !Catalog.Value.TryGetValue(originalPath, out var original)
                || AssetDatabase.AssetPathToGUID(originalPath) != original.Guid)
                return;
            Domain[] domains;
            if (srp)
            {
                // The URP adapter changes only the LightMode of the existing stock pass.
                // Match its original catalog entry using the preserved shader and pass identity.
                domains = original.Passes
                    .Where(entry => entry.Key.Item2 == name && entry.Key.Item1 is "FORWARDBASE" or "ALWAYS" or "VERTEX")
                    .SelectMany(entry => entry.Value)
                    .Where(domain => domain.Subshader == subshader && domain.Pass == snippet.pass.PassIndex)
                    .ToArray();
                if (domains.Length != 1) return;
                mode = "URP";
            }
            else if (!original.Passes.TryGetValue((mode, name), out domains)
                || !domains.Any(domain => domain.Subshader == subshader && domain.Pass == snippet.pass.PassIndex))
                return;

            int removed = 0;
            for (int index = data.Count - 1; index >= 0; index--)
            {
                var variant = data[index];
                if (!IsRecoveredPlatform(variant)) continue;
                var active = variant.shaderKeywordSet.GetShaderKeywords();
                if (domains.Any(domain => domain.Contains(active, variant.platformKeywordSet))) continue;
                data.RemoveAt(index);
                removed++;
            }
            if (removed != 0)
                Debug.Log($"NATIVEH3VR_SHADER_VARIANT_FILTER shader={path} pass={mode}/{name} stage={snippet.shaderType} stripped={removed}");
        }

        private static bool IsRecoveredPlatform(ShaderCompilerData variant)
        {
            switch (variant.buildTarget)
            {
                case BuildTarget.StandaloneOSX: return variant.shaderCompilerPlatform == ShaderCompilerPlatform.Metal;
                case BuildTarget.StandaloneWindows64: return variant.shaderCompilerPlatform == ShaderCompilerPlatform.D3D;
                case BuildTarget.StandaloneLinux64: return variant.shaderCompilerPlatform == ShaderCompilerPlatform.Vulkan;
                default: return false;
            }
        }

        private static string LightMode(PassType type)
        {
            switch (type)
            {
                case PassType.Normal: return "ALWAYS";
                case PassType.Vertex: return "VERTEX";
                case PassType.VertexLM: return "VERTEXLM";
                case PassType.ForwardBase: return "FORWARDBASE";
                case PassType.ForwardAdd: return "FORWARDADD";
                case PassType.ShadowCaster: return "SHADOWCASTER";
                case PassType.Deferred: return "DEFERRED";
                case PassType.Meta: return "META";
                case PassType.MotionVectors: return "MOTIONVECTORS";
                default: return null;
            }
        }

        private static Dictionary<string, KnownShader> LoadCatalog()
        {
            var shaders = new Dictionary<string, KnownShader>(StringComparer.Ordinal);
            var file = Path.Combine(Application.dataPath, "Editor/H3VRConverter/shader-variant-domains.json");
            if (!File.Exists(file)) return shaders;
            CatalogData document;
            try { document = JsonUtility.FromJson<CatalogData>(File.ReadAllText(file)); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                Debug.LogWarning($"NativeH3VR could not read its shader variant catalog. {error.Message}");
                return shaders;
            }
            if (document == null || document.schemaVersion != 1 || document.shaders == null) return shaders;
            foreach (var entry in document.shaders.Where(row => row != null && !string.IsNullOrEmpty(row.path))
                .GroupBy(row => row.path, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single()))
            {
                if (entry.guid == null || entry.guid.Length != 32 || entry.passes == null) continue;
                var original = new KnownShader(entry.guid);
                foreach (var pass in entry.passes.Where(row => row != null && !string.IsNullOrEmpty(row.lightMode) && row.name != null)
                    .GroupBy(row => (row.lightMode, row.name)).Where(group => group.Count() == 1).Select(group => group.Single()))
                {
                    if (pass.domains == null || pass.domains.Length == 0) continue;
                    var domains = pass.domains.Select(Domain.Create).ToArray();
                    if (domains.Any(domain => domain == null)
                        || domains.Select(domain => (domain.Subshader, domain.Pass)).Distinct().Count() != domains.Length)
                        continue;
                    original.Passes.Add((pass.lightMode, pass.name), domains);
                }
                shaders.Add(entry.path, original);
            }
            return shaders;
        }

        private sealed class KnownShader
        {
            public readonly string Guid;
            public readonly Dictionary<(string, string), Domain[]> Passes = new Dictionary<(string, string), Domain[]>();

            public KnownShader(string guid) { Guid = guid; }
        }

        private sealed class Domain
        {
            public readonly uint Subshader;
            public readonly uint Pass;
            private readonly Dictionary<string, ulong> keywords;
            private readonly (BuiltinShaderDefine Keyword, ulong Mask)[] platformKeywords;
            private readonly ulong fixedMask;
            private readonly HashSet<ulong> rows;

            private Domain(DomainData source, Dictionary<string, ulong> keywords, ulong fixedMask, HashSet<ulong> rows)
            {
                Subshader = (uint)source.subshaderIndex;
                Pass = (uint)source.passIndex;
                this.keywords = keywords;
                var builtins = new List<(BuiltinShaderDefine, ulong)>();
                foreach (var keyword in keywords)
                    if (Enum.TryParse(keyword.Key, out BuiltinShaderDefine mode) && mode.ToString() == keyword.Key)
                        builtins.Add((mode, keyword.Value));
                platformKeywords = builtins.ToArray();
                this.fixedMask = fixedMask;
                this.rows = rows;
            }

            internal static Domain Create(DomainData source)
            {
                if (source == null || source.subshaderIndex < 0 || source.passIndex < 0 || source.keywords == null
                    || source.keywords.Length == 0 || source.keywords.Length > 64 || source.@fixed == null
                    || source.rows == null || source.rows.Length == 0)
                    return null;
                var masks = new Dictionary<string, ulong>(StringComparer.Ordinal);
                for (int index = 0; index < source.keywords.Length; index++)
                    if (string.IsNullOrEmpty(source.keywords[index]) || !masks.TryAdd(source.keywords[index], 1UL << index))
                        return null;
                ulong constant = 0;
                foreach (var keyword in source.@fixed)
                {
                    if (keyword == null || !masks.TryGetValue(keyword, out ulong bit)) return null;
                    constant |= bit;
                }
                ulong all = source.keywords.Length == 64 ? ulong.MaxValue : (1UL << source.keywords.Length) - 1;
                var rows = new HashSet<ulong>();
                foreach (var key in source.rows)
                    if (!ulong.TryParse(key, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong row)
                        || (row & ~all) != 0 || (row & constant) != constant || !rows.Add(row))
                        return null;
                return new Domain(source, masks, constant, rows);
            }

            internal bool Contains(ShaderKeyword[] active, PlatformKeywordSet platform)
            {
                ulong mask = fixedMask;
                foreach (var keyword in active)
                {
                    if (string.IsNullOrEmpty(keyword.name)) return true;
                    if (keywords.TryGetValue(keyword.name, out ulong bit)) mask |= bit;
                }
                // Some stock domains include Unity-supplied flags such as UNITY_COLORSPACE_GAMMA.
                foreach (var keyword in platformKeywords)
                    if (platform.IsEnabled(keyword.Keyword)) mask |= keyword.Mask;
                return rows.Contains(mask);
            }
        }

        [Serializable]
        private sealed class CatalogData { public int schemaVersion; public ShaderData[] shaders; }

        [Serializable]
        private sealed class ShaderData { public string path; public string guid; public PassData[] passes; }

        [Serializable]
        private sealed class PassData { public string lightMode; public string name; public DomainData[] domains; }

        [Serializable]
        private sealed class DomainData
        {
            public int subshaderIndex = -1;
            public int passIndex = -1;
            public string[] keywords;
            public string[] @fixed;
            public string[] rows;
        }
    }
}
