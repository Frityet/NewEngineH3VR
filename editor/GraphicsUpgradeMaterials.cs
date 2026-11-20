#if NATIVEH3VR_GRAPHICS_UPGRADE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private const string LegacyDirectionalShadow = "Assets/NativeH3VR/GraphicsUpgrade/Shaders/LegacyDirectionalShadow.hlsl";
        private static readonly Regex BuiltinDefault = new Regex(
            @"\{fileID: 10303, guid: 0000000000000000f000000000000000, type: 0\}",
            RegexOptions.CultureInvariant);
        private const string TerrainLitName = "Universal Render Pipeline/Terrain/Lit";
        private const string StockWarmupPath = "Assets/ShaderVariantCollection/H3_ShaderAsset3.shadervariants";
        private const string InclusionDirectory = "Assets/Resources/NativeH3VR/ShaderVariants";
        private const string InclusionPath = InclusionDirectory + "/H3_ShaderAsset3Full.shadervariants";
        private static readonly Regex VariantGroup = new Regex(
            @"(?m)^  - first: \{fileID: (?:0|-?\d+, guid: [a-f0-9]{32}, type: \d+)\}\r?\n    second:\r?\n      variants:\r?\n",
            RegexOptions.CultureInvariant);
        private static readonly Regex Variant = new Regex(
            @"(?m)^      - keywords:[^\r\n]*\r?\n        passType: (?<pass>-?\d+)(?:\r?\n|$)",
            RegexOptions.CultureInvariant);

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
        private static readonly string[] LegacyShadowReceivers =
        {
            "Alloy Mods/Transition Lite",
            "Alloy/Car Paint",
            "Alloy/Core",
            "Alloy/Directional Blend",
            "Alloy/Nature/SpeedTree (Forward)",
            "Alloy/Nature/Terrain/4Splat",
            "Alloy/Oriented/Core",
            "Alloy/Prototyping",
            "Alloy/TriPlanar",
            "Alloy/Vertex Blend",
            "Alloy/Weighted Blend",
            "CynCliff HeightBlend",
            "Hidden/Alloy/Nature/Terrain/Details0",
            "Hidden/Alloy/Nature/Terrain/Details1",
            "Hidden/Alloy/Nature/Terrain/Details2",
            "Hidden/Alloy/Nature/Terrain/Distant",
            "NatureManufacture Shaders/Cross Model Shader",
            "NatureManufacture Shaders/Tree Bark Metalic",
            "NatureManufacture Shaders/Tree_Leaves_Specular",
        };
        private static readonly string[] AlloyFeatures =
        {
            "_ParallaxT", "_AO2", "_TeamColor", "_Decal", "_Rim", "_Dissolve",
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
        private sealed class WarmupReport
        {
            public int originalVariants;
            public int retainedVariants;
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
            public WarmupReport shaderWarmup;
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
            var shaderWarmup = FilterStockShaderWarmup(changed);
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
                retaggedShaders = retaggedShaders,
                shaderWarmup = shaderWarmup
            };
            File.WriteAllText("ProjectSettings/NativeH3VRGraphicsMaterials.json", JsonUtility.ToJson(report, true) + "\n");
            Debug.Log($"NATIVEH3VR_GRAPHICS_MATERIALS converted={families.Values.Sum(family => family.converted)} retained={families.Values.Sum(family => family.retained)} placeholders={placeholders} defaultReferences={replacedDefaultReferences} retaggedShaders={retaggedShaders}");
        }

        private static WarmupReport FilterStockShaderWarmup(SortedSet<string> changed)
        {
            var stock = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(StockWarmupPath);
            if (stock == null || stock.name != "H3_ShaderAsset3")
                return null;

            string current = File.ReadAllText(StockWarmupPath);
            bool hasFullCopy = File.Exists(InclusionPath);
            string original = hasFullCopy ? File.ReadAllText(InclusionPath) : current;
            if (!TryFilterWarmup(original, out var filtered, out var report) ||
                (current != original && current != filtered))
                return null;
            if (report.originalVariants == report.retainedVariants)
                return report;

            // Resources keeps the complete collection in the build. The stock collection
            // retains its identity for both the Graphics Settings preload and GM.Awake.
            if (!hasFullCopy)
            {
                Directory.CreateDirectory(InclusionDirectory);
                File.Copy(StockWarmupPath, InclusionPath);
                AssetDatabase.ImportAsset(InclusionPath, ImportAssetOptions.ForceSynchronousImport);
                changed.Add(InclusionPath);
            }
            if (current != filtered)
            {
                File.WriteAllText(StockWarmupPath, filtered);
                AssetDatabase.ImportAsset(StockWarmupPath, ImportAssetOptions.ForceSynchronousImport);
                changed.Add(StockWarmupPath);
            }

            Debug.Log($"NATIVEH3VR_GRAPHICS_SHADER_WARMUP original={report.originalVariants} retained={report.retainedVariants}");
            return report;
        }

        private static bool TryFilterWarmup(string original, out string filtered, out WarmupReport report)
        {
            filtered = null;
            report = new WarmupReport();
            var groups = VariantGroup.Matches(original);
            if (groups.Count == 0)
                return false;

            string header = original.Substring(0, groups[0].Index);
            if (!header.EndsWith("  m_Shaders:\n", StringComparison.Ordinal) &&
                !header.EndsWith("  m_Shaders:\r\n", StringComparison.Ordinal))
                return false;

            var result = new StringBuilder(header);
            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                int start = group.Index + group.Length;
                int end = index + 1 == groups.Count ? original.Length : groups[index + 1].Index;
                var variants = Variant.Matches(original.Substring(start, end - start));
                if (variants.Count == 0)
                    return false;

                int consumed = 0;
                bool wroteGroup = false;
                foreach (Match entry in variants)
                {
                    if (entry.Index != consumed || !int.TryParse(entry.Groups["pass"].Value, out int pass))
                        return false;
                    consumed += entry.Length;
                    report.originalVariants++;
                    // Retagged forward passes have new warmup keys in URP. ForwardAdd
                    // and Meta have no runtime draw here. Keep unknown pass IDs.
                    if (pass == (int)PassType.Vertex || pass == (int)PassType.ForwardBase ||
                        pass == (int)PassType.ForwardAdd || pass == (int)PassType.Meta)
                        continue;
                    if (!wroteGroup)
                    {
                        result.Append(group.Value);
                        wroteGroup = true;
                    }
                    result.Append(entry.Value);
                    report.retainedVariants++;
                }
                if (start + consumed != end)
                    return false;
            }
            filtered = result.ToString();
            return report.retainedVariants != 0;
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
                    migrated = LegacyVertexLightBridge.Apply(source, migrated, AssetDatabase.AssetPathToGUID(path), changed);
                    if (LegacyShadowReceivers.Any(name => source.StartsWith("Shader \"" + name + "\"", StringComparison.Ordinal)))
                        migrated = AddLegacyDirectionalShadows(migrated, AssetDatabase.AssetPathToGUID(path), changed);
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

        private static string AddLegacyDirectionalShadows(string source, string shaderGuid, SortedSet<string> changed)
        {
            // The recovered forward pass contains distinct baked and realtime programs.
            // The overlay may store its variants inline or in pass-specific includes.
            const string passStart = "#define DIRECTIONAL 1";
            int start = source.IndexOf(passStart, StringComparison.Ordinal);
            if (start < 0)
                return source;
            int end = source.IndexOf("ENDCG", start, StringComparison.Ordinal);
            if (end < 0)
                return source;
            string forward = source.Substring(start, end - start);
            if (forward.Contains(LegacyDirectionalShadow))
                return source;

            string prefix = "Assets/NativeH3VRShaderPrograms/" + shaderGuid + "/pass-0-";
            string adaptedForward = AddLegacyShadowTerms(forward);
            bool adapted = adaptedForward != forward;
            foreach (Match include in Regex.Matches(forward, "#include \"(" + Regex.Escape(prefix) + "[0-9]+\\.cginc)\""))
            {
                string path = include.Groups[1].Value;
                if (!File.Exists(path))
                    continue;
                string program = File.ReadAllText(path);
                if (program.Contains(" * StockShadowStrength("))
                {
                    adapted = true;
                    continue;
                }
                string migrated = AddLegacyShadowTerms(program);
                if (migrated == program)
                    continue;
                File.WriteAllText(path, migrated);
                AssetDatabase.ImportAsset(path);
                changed.Add(path);
                adapted = true;
            }
            if (!adapted)
                return source;

            string bridge = "\n#pragma dynamic_branch _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE" +
                "\n#include \"" + LegacyDirectionalShadow + "\"";
            return source.Substring(0, start) + adaptedForward.Insert(passStart.Length, bridge) + source.Substring(end);
        }

        private static string AddLegacyShadowTerms(string program)
        {
            return Regex.Replace(program,
                @"(?ms)^[ \t]*#(?:if|elif) defined\(DIRECTIONAL\)(?<keywords>[^\r\n]*)\r?\n(?<body>.*?)(?=^[ \t]*#(?:if|elif) defined\(DIRECTIONAL\)|\z)",
                AddLegacyShadowTerm);
        }

        private static string AddLegacyShadowTerm(Match branch)
        {
            string keywords = branch.Groups["keywords"].Value;
            if (!keywords.Contains("!defined(SHADOWS_SCREEN)") || !keywords.Contains("!defined(LIGHTMAP_ON)") ||
                !keywords.Contains("!defined(SHADOWS_SHADOWMASK)") || !keywords.Contains("!defined(LIGHTMAP_SHADOW_MIXING)") ||
                !keywords.Contains("!defined(UNITY_SINGLE_PASS_STEREO)"))
                return branch.Value;

            string body = branch.Groups["body"].Value;
            const string light = "_LightColor0.xyz";
            string position = LegacyShadowPosition(body);
            if (position == null)
                return branch.Value;
            var direct = Regex.Match(body, @"(?m)^[ \t]*tmp\d+\.xyz = (?<color>_LightColor0\.xyz) \* tmp\d+\.xyz \+ tmp\d+\.(?:xyz|yzw);");
            int index = body.IndexOf(light, StringComparison.Ordinal);
            if (!direct.Success || index != direct.Groups["color"].Index ||
                body.LastIndexOf(light, StringComparison.Ordinal) != index)
                return branch.Value;
            string expression = "(" + light + " * StockShadowStrength(" + position + "))";
            int relative = branch.Groups["body"].Index - branch.Index + index;
            return branch.Value.Remove(relative, light.Length).Insert(relative, expression);
        }

        private static string LegacyShadowPosition(string body)
        {
            const string separatePosition = "inp.texcoord3.xyz";
            if (body.Contains("_WorldSpaceCameraPos - " + separatePosition))
                return separatePosition;

            // The cliff and vegetation programs pack world position in the tangent
            // frame interpolators. Verify the fragment reconstructs it for the view vector.
            const string line = @"\r?\n[ \t]*";
            string packedStart = @"(?m)^[ \t]*tmp(\d+)\.";
            string packedEnd = @"tmp\d+\.(?:xyz|yzw) = _WorldSpaceCameraPos - tmp\1\.";
            bool packed = Regex.IsMatch(body, packedStart + @"x = inp\.texcoord1\.w;" + line +
                @"tmp\1\.y = inp\.texcoord2\.w;" + line + @"tmp\1\.z = inp\.texcoord3\.w;" + line + packedEnd + "xyz;") ||
                Regex.IsMatch(body, packedStart + @"y = inp\.texcoord1\.w;" + line +
                @"tmp\1\.z = inp\.texcoord2\.w;" + line + @"tmp\1\.w = inp\.texcoord3\.w;" + line + packedEnd + "yzw;");
            return packed ? "float3(inp.texcoord1.w, inp.texcoord2.w, inp.texcoord3.w)" : null;
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

        private static bool CanConvertAlloyDetail(Material material)
        {
            bool detailEnabled = material.HasProperty("_DetailT") && material.GetFloat("_DetailT") > 0.5f;
            if (detailEnabled != material.IsKeywordEnabled("_DETAIL_MULX2"))
                return false;
            if (!detailEnabled)
                return true;

            float uvSet = material.GetFloat("_DetailAlbedoMapUV");
            float maskSource = material.GetFloat("_DetailMaskSource");
            float blendMode = material.GetFloat("_DetailMode");
            return (uvSet == 0 || uvSet == 1) &&
                (maskSource == 0 || maskSource == 1) &&
                (blendMode == 0 || blendMode == 1) &&
                (maskSource == 1) == material.IsKeywordEnabled("_NORMALMAP");
        }

        private static bool ConvertAlloy(Material material, Shader stockPbr)
        {
            // Restrict PBR migration to the common opaque and alpha-test recipes. Dynamic
            // effects, special UV layouts, decals and transparent queues retain stock passes.
            int mode = Mathf.RoundToInt(material.GetFloat("_Mode"));
            if (mode < 0 || mode > 1 || material.renderQueue > (int)RenderQueue.GeometryLast ||
                material.GetFloat("_ZWrite") < 0.5f ||
                !string.IsNullOrEmpty(material.GetTag("NativeH3VRLegacyHdrColor", false)) ||
                AlloyFeatures.Any(property => material.HasProperty(property) && material.GetFloat(property) > 0.5f) ||
                !CanConvertAlloyDetail(material))
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
