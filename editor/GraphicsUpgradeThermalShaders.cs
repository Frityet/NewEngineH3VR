#if NATIVEH3VR_GRAPHICS_UPGRADE
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace H3VRConverter
{
    internal static class GraphicsUpgradeThermalShaders
    {
        private const string Generated = "Assets/NativeH3VR/GraphicsUpgrade/Generated";
        private const string SourcePathRecord = Generated + "/ThermalShaderSource.txt";
        private const string StockName = "Hidden/Thermal";
        private const string NamePrefix = "Hidden/NativeH3VR/Thermal/";
        private const string SelectorName = "Hidden/NativeH3VR/StockThermalSelectors/";
        private const string TagPrefix = "NativeH3VRThermal";
        private static readonly string[] Types = { "Opaque", "Transparent", "TransparentCutout" };
        private static readonly Regex Subshader = new Regex(
            @"(?m)^[ \t]*SubShader\s*(?<open>\{)", RegexOptions.CultureInvariant);
        private static readonly Regex RenderType = new Regex(
            @"""RenderType""\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant);
        private static readonly Regex ShaderName = new Regex(
            @"(?m)^[ \t]*Shader\s+""([^""]+)""", RegexOptions.CultureInvariant);
        private static readonly Regex ForwardMode = new Regex(
            @"(?i)(""LightMode""\s*=\s*"")ForwardBase""", RegexOptions.CultureInvariant);
        private static readonly Regex Include = new Regex(
            @"(?m)(^[ \t]*#include(?:_with_pragmas)?[ \t]+"")([^""]+)""", RegexOptions.CultureInvariant);

        internal static string PathFor(string type) => Generated + "/Thermal" + type + ".shader";

        internal static void Prepare()
        {
            var original = Shader.Find(StockName);
            if (!original)
                throw new InvalidOperationException("The recovered stock thermal shader is missing.");
            string path = AssetDatabase.GetAssetPath(original);
            string source = File.ReadAllText(path);
            var blocks = Subshader.Matches(source);
            if (blocks.Count != Types.Length || ShaderName.Match(source).Groups[1].Value != StockName)
                throw new InvalidOperationException("Unrecognized stock thermal replacement shader.");
            Directory.CreateDirectory(Generated);
            File.WriteAllText(SourcePathRecord, path + "\n");
            string prefix = source.Substring(0, blocks[0].Index);
            for (int index = 0; index < blocks.Count; index++)
            {
                int start = blocks[index].Index;
                int end = ClosingBrace(source, blocks[index].Groups["open"].Index);
                string block = source.Substring(start, end - start + 1);
                if (RenderType.Match(block).Groups[1].Value != Types[index])
                    throw new InvalidOperationException("Stock thermal shader RenderType order changed.");
                string shader = prefix.Replace("\"" + StockName + "\"", "\"" + NamePrefix + Types[index] + "\"") +
                    ForwardMode.Replace(block, "$1UniversalForward\"") + "\n}\n";
                string destination = PathFor(Types[index]);
                File.WriteAllText(destination, shader);
                AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
            }
        }

        internal static bool TryMatchBranch(string path, out string originalPath, out int subshader)
        {
            subshader = Array.FindIndex(Types, type => path == PathFor(type));
            originalPath = subshader >= 0 && File.Exists(SourcePathRecord) ? File.ReadAllText(SourcePathRecord).Trim() : null;
            return !string.IsNullOrEmpty(originalPath);
        }

        internal static string MarkSource(string source)
        {
            string shaderName = ShaderName.Match(source).Groups[1].Value;
            if (shaderName == StockName || shaderName == "Hidden/PIPScope_PostProcess" ||
                source.Contains(SelectorName)) return source;
            foreach (Match block in Subshader.Matches(source).Cast<Match>().Reverse())
            {
                int closing = ClosingBrace(source, block.Groups["open"].Index);
                string passes = string.Concat(Types.Select(type =>
                    "\n        UsePass \"" + SelectorName + type.ToUpperInvariant() + "\""));
                source = source.Insert(closing, passes + "\n    ");
            }
            return source;
        }

        internal static bool ConfigureMaterial(Material material, string originalType)
        {
            bool changed = false;
            string shaderPath = AssetDatabase.GetAssetPath(material.shader);
            if (shaderPath.StartsWith("Packages/com.unity.render-pipelines.universal/", StringComparison.Ordinal) &&
                Types.Contains(originalType))
            {
                // Unity package sources stay immutable. Import a local twin with the
                // same properties and ordinary passes so the material can opt in.
                string destination = Generated + "/ThermalInput_" + Hash128.Compute(shaderPath) + ".shader";
                var marked = AssetDatabase.LoadAssetAtPath<Shader>(destination);
                if (!marked)
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(shaderPath);
                    string original = Path.Combine(package.resolvedPath, shaderPath.Substring(package.assetPath.Length + 1));
                    string source = File.ReadAllText(original);
                    string token = "Shader \"" + material.shader.name + "\"";
                    if (!source.Contains(token))
                        throw new InvalidOperationException("Could not identify URP shader source " + shaderPath);
                    string markedSource = source.Replace(token, "Shader \"Hidden/NativeH3VR/ThermalInput/" + material.shader.name + "\"");
                    markedSource = Include.Replace(markedSource, include =>
                    {
                        string name = include.Groups[2].Value;
                        string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(original), name));
                        if (!File.Exists(local)) return include.Value;
                        string relative = Path.GetRelativePath(package.resolvedPath, local).Replace('\\', '/');
                        if (relative.StartsWith("../", StringComparison.Ordinal))
                            throw new InvalidOperationException("URP shader include escapes its package " + name);
                        return include.Groups[1].Value + package.assetPath + "/" + relative + "\"";
                    });
                    File.WriteAllText(destination, MarkSource(markedSource));
                    AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
                    marked = AssetDatabase.LoadAssetAtPath<Shader>(destination);
                    if (!marked || ShaderUtil.ShaderHasError(marked))
                        throw new InvalidOperationException("Thermal-compatible URP shader failed to import " + shaderPath);
                }
                int rawQueue = material.rawRenderQueue;
                material.shader = marked;
                material.renderQueue = rawQueue;
                changed = true;
            }
            foreach (string type in Types)
            {
                string tag = TagPrefix + type;
                bool enable = originalType == type;
                if (material.GetShaderPassEnabled(tag) == enable) continue;
                material.SetShaderPassEnabled(tag, enable);
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(material);
            return changed;
        }

        internal static void PrepareStockPbr()
        {
            const string path = "Assets/NativeH3VR/GraphicsUpgrade/Shaders/StockPbr.shader";
            string source = File.ReadAllText(path);
            string marked = MarkSource(source);
            if (source == marked) return;
            File.WriteAllText(path, marked);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static int ClosingBrace(string source, int open)
        {
            int depth = 1;
            for (int index = open + 1; index < source.Length; index++)
            {
                char current = source[index];
                if (current == '"')
                {
                    while (++index < source.Length && source[index] != '"')
                        if (source[index] == '\\') index++;
                }
                else if (current == '/' && index + 1 < source.Length)
                {
                    if (source[index + 1] == '/')
                    {
                        while (++index < source.Length && source[index] != '\n') { }
                    }
                    else if (source[index + 1] == '*')
                    {
                        index = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                        if (index < 0) break;
                        index++;
                    }
                }
                else if (current == '{') depth++;
                else if (current == '}' && --depth == 0) return index;
            }
            throw new InvalidOperationException("Unclosed ShaderLab SubShader.");
        }
    }
}
#endif
