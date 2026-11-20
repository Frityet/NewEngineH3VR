#if NATIVEH3VR_GRAPHICS_UPGRADE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace H3VRConverter
{
    internal static class GraphicsUpgradeMaterials
    {
        private const string StockPbrName = "NativeH3VR/Stock PBR";
        private const string GeneratedDefault = "Assets/NativeH3VR/GraphicsUpgrade/Generated/StockDefault.mat";
        private static readonly Regex BuiltinDefault = new Regex(
            @"\{fileID: 10303, guid: 0000000000000000f000000000000000, type: 0\}",
            RegexOptions.CultureInvariant);
        private const string TerrainLitName = "Universal Render Pipeline/Terrain/Lit";
        private static readonly Regex LegacyPass = new Regex(
            @"(?<prefix>""LightMode""[ 	]*=[ 	]*"")(?<mode>ForwardBase|Always|Vertex|VertexLM|VertexLMRGBM|PrepassBase)""",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Dictionary<string, string> LegacyPassModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ForwardBase"] = "UniversalForward",
            ["Always"] = "UniversalForwardOnly",
            ["Vertex"] = "UniversalForwardOnly",
            ["VertexLM"] = "NativeH3VRLegacyVertexLM",
            ["VertexLMRGBM"] = "NativeH3VRLegacyVertexLMRGBM",
            ["PrepassBase"] = "NativeH3VRLegacyPrepassBase",
        };
        private static readonly string[] AlloyFeatures =
        {
            "_ParallaxT", "_AO2", "_DetailT", "_TeamColor", "_Decal", "_Rim", "_Dissolve",
            "_WetnessProperties", "_MainTexUV", "_IncandescenceMapUV",
        };

        [Serializable]
        private sealed class Family
        {
            public string shader;
            public int converted;
            public int retained;
        }

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 1;
            public string[] changedAssets;
            public Family[] families;
            public int placeholderMaterials;
            public int replacedDefaultReferences;
            public int retaggedShaders;
        }

        public static void Apply()
        {
            var stockPbr = Shader.Find(StockPbrName);
            if (stockPbr == null || ShaderUtil.ShaderHasError(stockPbr))
                throw new InvalidOperationException("Stock PBR shader failed to import: " + StockPbrName);
            var urpLit = Shader.Find("Universal Render Pipeline/Lit");
            var terrainLit = Shader.Find(TerrainLitName);
            if (urpLit == null || terrainLit == null)
                throw new InvalidOperationException("Universal Render Pipeline material shaders failed to import.");

            var upgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset));
            var changed = new SortedSet<string>(StringComparer.Ordinal);
            var families = new SortedDictionary<string, Family>(StringComparer.Ordinal);
            int placeholders = 0;
            int retaggedShaders = RetagLegacyPasses(changed);
            // Standalone and sub-asset materials are shared by scenes, Resources and the
            // rebuilt bundles. Mutate the objects in place to preserve asset identities.
            var paths = AssetDatabase.FindAssets("t:Material", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                foreach (var material in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
                {
                    var shader = material.shader;
                    if (shader == null || shader.name == "Hidden/InternalErrorShader")
                    {
                        placeholders++;
                        continue;
                    }
                    if (!families.TryGetValue(shader.name, out var family))
                    {
                        family = new Family { shader = shader.name };
                        families.Add(shader.name, family);
                    }

                    if (!Convert(material, stockPbr, terrainLit, upgraders))
                    {
                        family.retained++;
                        continue;
                    }
                    family.converted++;
                    EditorUtility.SetDirty(material);
                    changed.Add(path);
                }
            }

            int replacedDefaultReferences = ReplaceBuiltinDefaults(urpLit, changed);
            AssetDatabase.SaveAssets();
            var report = new Report
            {
                changedAssets = changed.ToArray(),
                families = families.Values.ToArray(),
                placeholderMaterials = placeholders,
                replacedDefaultReferences = replacedDefaultReferences,
                retaggedShaders = retaggedShaders
            };
            File.WriteAllText("ProjectSettings/NativeH3VRGraphicsMaterials.json", JsonUtility.ToJson(report, true) + "\n");
            Debug.Log($"NATIVEH3VR_GRAPHICS_MATERIALS converted={families.Values.Sum(family => family.converted)} retained={families.Values.Sum(family => family.retained)} placeholders={placeholders} defaultReferences={replacedDefaultReferences} retaggedShaders={retaggedShaders}");
        }

        private static int RetagLegacyPasses(SortedSet<string> changed)
        {
            int count = 0;
            var paths = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
                .Where(path => path.EndsWith(".shader", StringComparison.Ordinal) &&
                               !path.StartsWith("Assets/NativeH3VR/GraphicsUpgrade/", StringComparison.Ordinal));
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in paths)
                {
                    string source = File.ReadAllText(path);
                    string migrated = LegacyPass.Replace(source, pass =>
                        pass.Groups["prefix"].Value + LegacyPassModes[pass.Groups["mode"].Value] + "\"");
                    if (migrated == source)
                        continue;
                    // Keep original shader identity, render state, program, pass order and
                    // material properties. Legacy-only tags trigger URP's error material draw.
                    File.WriteAllText(path, migrated);
                    AssetDatabase.ImportAsset(path);
                    changed.Add(path);
                    count++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            return count;
        }

        private static int ReplaceBuiltinDefaults(Shader urpLit, SortedSet<string> changed)
        {
            string replacement = null;
            int count = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) &&
                           (path.EndsWith(".prefab", StringComparison.Ordinal) || path.EndsWith(".unity", StringComparison.Ordinal))))
                {
                    string source = File.ReadAllText(path);
                    if (!BuiltinDefault.IsMatch(source))
                        continue;
                    replacement ??= CreateDefaultReference(urpLit, changed);
                    string migrated = BuiltinDefault.Replace(source, _ =>
                    {
                        count++;
                        return replacement;
                    });
                    File.WriteAllText(path, migrated);
                    AssetDatabase.ImportAsset(path);
                    changed.Add(path);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            return count;
        }

        private static string CreateDefaultReference(Shader urpLit, SortedSet<string> changed)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(GeneratedDefault);
            if (material == null)
            {
                material = new Material(urpLit) { name = "Stock Default Material" };
                AssetDatabase.CreateAsset(material, GeneratedDefault);
                changed.Add(GeneratedDefault);
            }
            else if (material.shader != urpLit)
            {
                material.shader = urpLit;
                EditorUtility.SetDirty(material);
                changed.Add(GeneratedDefault);
            }
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId))
                throw new InvalidOperationException("Could not create a persistent stock default material reference.");
            return $"{{fileID: {localId}, guid: {guid}, type: 2}}";
        }

        private static bool Convert(Material material, Shader stockPbr, Shader terrainLit,
            List<MaterialUpgrader> upgraders)
        {
            if (material.parent != null)
                return false;
            if (material.shader.name == "Alloy/Core")
                return ConvertAlloy(material, stockPbr);

            var original = material.shader;
            if (original.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                return false;
            if (original.name == "Nature/Terrain/Specular")
            {
                material.shader = terrainLit;
                return true;
            }

            // Use Unity's own migration for built-in shader families. Stock custom
            // particle, UI, skybox, optic and terrain programs retain their bindings.
            string message = null;
            int rawQueue = material.rawRenderQueue;
            if (!MaterialUpgrader.Upgrade(material, upgraders, MaterialUpgrader.UpgradeFlags.None, ref message) ||
                material.shader == original)
                return false;
            if (rawQueue >= 0)
                material.renderQueue = rawQueue;
            return true;
        }

        private static bool ConvertAlloy(Material material, Shader stockPbr)
        {
            // Restrict PBR migration to the common opaque and alpha-test recipes. Dynamic
            // effects, special UV layouts, decals and transparent queues retain stock passes.
            int mode = Mathf.RoundToInt(material.GetFloat("_Mode"));
            if (mode < 0 || mode > 1 || material.renderQueue > (int)RenderQueue.GeometryLast ||
                material.GetFloat("_ZWrite") < 0.5f ||
                !string.IsNullOrEmpty(material.GetTag("NativeH3VRLegacyHdrColor", false)) ||
                AlloyFeatures.Any(property => material.HasProperty(property) && material.GetFloat(property) > 0.5f))
                return false;

            int queue = material.renderQueue;
            material.shader = stockPbr;
            material.renderQueue = queue;
            material.SetOverrideTag("RenderType", mode == 1 ? "TransparentCutout" : "Opaque");
            material.shaderKeywords = Array.Empty<string>();
            if (mode == 1) material.EnableKeyword("_ALPHATEST_ON");
            return true;
        }
    }
}
#endif
