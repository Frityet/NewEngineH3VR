using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

// Keep exact original paired programs. Never silently substitute a synthetic
// keyword combination. Call Configure() before Player and AssetBundle builds.
public sealed class NativeOriginalVariantFilter : IPreprocessShaders {
    public int callbackOrder {
        get { return int.MaxValue; }
    }
    sealed class Pass {
        public string name;
        public PassType type;
        public int subshader;
        public HashSet<string> names, allowed;
        public string identity {
            get { return type + "/" + name; }
        }
    }
    static readonly Dictionary<string, Dictionary<string, Pass>> cache =
        new Dictionary<string, Dictionary<string, Pass>>();
    static bool IsRecoveredSource(string source) {
        return source.Contains("Original shader has no recovered variant") ||
               source.Contains("Assets/NativeShaders/CameraRelativeVariants/") ||
               source.Contains("Assets/NativeShaders/CliffVariants/") ||
               source.Contains("Assets/NativeShaders/ResidualVariants/");
    }
    static PassType OriginalPassType(string mode) {
        switch (mode.ToUpperInvariant()) {
        case "SHADOWCOLLECTOR":
            return (PassType)9;
        case "ALWAYS":
            return PassType.Normal;
        case "VERTEX":
            return PassType.Vertex;
        case "FORWARDBASE":
            return PassType.ForwardBase;
        case "FORWARDADD":
            return PassType.ForwardAdd;
        case "DEFERRED":
            return PassType.Deferred;
        case "META":
            return PassType.Meta;
        case "SHADOWCASTER":
            return PassType.ShadowCaster;
        default:
            throw new BuildFailedException("Unmapped original LightMode " + mode);
        }
    }
    static Pass[] ReadPasses(string source) {
        var result = new List<Pass>();
        foreach (Match match in Regex.Matches(
                     source, @"\bPass\s*\{(?<header>.*?)CGPROGRAM(?<program>.*?)ENDCG",
                     RegexOptions.Singleline)) {
            string header = match.Groups["header"].Value, body = match.Groups["program"].Value;
            var name = Regex.Match(header, @"\bName\s+""([^""]*)""");
            var mode =
                Regex.Match(header, @"""LIGHTMODE""\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            var keys =
                Regex.Matches(body, @"^\s*#(?:if|elif) (.+)$", RegexOptions.Multiline)
                    .Cast<Match>()
                    .Select(m => string.Join(
                                " ", Regex.Matches(m.Groups[1].Value, @"(?<!!)defined\((\w+)\)")
                                         .Cast<Match>()
                                         .Select(k => k.Groups[1].Value)
                                         .OrderBy(k => k, StringComparer.Ordinal)))
                    .ToArray();
            if (keys.Length == 0)
                throw new BuildFailedException("Missing original paired variant guards");
            result.Add(new Pass {
                name = name.Success ? name.Groups[1].Value : "",
                type = OriginalPassType(mode.Success ? mode.Groups[1].Value : "ALWAYS"),
                subshader =
                    Regex.Matches(source.Substring(0, match.Index), @"\bSubShader\s*\{").Count - 1,
                allowed = new HashSet<string>(keys),
                names = new HashSet<string>(keys.SelectMany(
                    k => k.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)))
            });
        }
        if (result.Count != Regex.Matches(source, "CGPROGRAM").Count)
            throw new BuildFailedException("Original pass/source count mismatch");
        if (result.Select(p => p.identity).Distinct().Count() != result.Count)
            throw new BuildFailedException(
                "Original pass identities are ambiguous; name previously unnamed passes without " +
                "changing their order/state");
        return result.ToArray();
    }
    public static void Configure() {
        string collectionPath =
            "Assets/NativeShaders/CameraRelativeVariants/OriginalVariants.shadervariants";
        var targets = AssetDatabase.FindAssets("t:Shader")
                          .Select(AssetDatabase.GUIDToAssetPath)
                          .Where(p => !p.StartsWith("Assets/Editor/", StringComparison.Ordinal) &&
                                      File.Exists(p) && IsRecoveredSource(File.ReadAllText(p)))
                          .ToArray();
        if (targets.Length == 0)
            return;
        var collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(collectionPath);
        bool create = collection == null;
        if (create)
            collection = new ShaderVariantCollection();
        else
            collection.Clear();
        var settings = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
        settings.FindProperty("m_LightmapStripping").intValue = 1;
        settings.FindProperty("m_FogStripping").intValue = 1;
        foreach (string name in new[] { "m_LightmapKeepPlain", "m_LightmapKeepDirCombined",
                                        "m_LightmapKeepDynamicPlain",
                                        "m_LightmapKeepDynamicDirCombined",
                                        "m_LightmapKeepShadowMask", "m_LightmapKeepSubtractive",
                                        "m_FogKeepLinear", "m_FogKeepExp", "m_FogKeepExp2" })
            settings.FindProperty(name).boolValue = true;
        var included = settings.FindProperty("m_AlwaysIncludedShaders");
        bool instancing = false;
        foreach (string path in targets) {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            bool found = false;
            for (int i = 0; i < included.arraySize; i++)
                if (included.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    found = true;
            if (!found) {
                included.arraySize++;
                included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue =
                    shader;
            }
            foreach (var pass in ReadPasses(File.ReadAllText(path))) {
                instancing |= pass.names.Contains("INSTANCING_ON");
                // SVC validates the active first supported subshader. Always Included and
                // the callbacks below also preserve/verify the original fallback subshaders.
                if (pass.subshader != 0)
                    continue;
                foreach (var key in pass.allowed)
                    collection.Add(new ShaderVariantCollection.ShaderVariant(
                        shader, pass.type,
                        key.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)));
            }
        }
        if (instancing)
            settings.FindProperty("m_InstancingStripping").intValue =
                2; // KeepAll; verified by missing=0 below.
        if (create) {
            Directory.CreateDirectory(Path.GetDirectoryName(collectionPath));
            AssetDatabase.CreateAsset(collection, collectionPath);
        } else
            EditorUtility.SetDirty(collection);
        var preloaded = settings.FindProperty("m_PreloadedShaders");
        bool exists = false;
        for (int i = 0; i < preloaded.arraySize; i++)
            if (preloaded.GetArrayElementAtIndex(i).objectReferenceValue == collection)
                exists = true;
        if (!exists) {
            preloaded.arraySize++;
            preloaded.GetArrayElementAtIndex(preloaded.arraySize - 1).objectReferenceValue =
                collection;
        }
        settings.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        cache.Clear();
        Debug.Log("NATIVE_ORIGINAL_VARIANT_CONFIGURATION shaders=" + targets.Length +
                  " variants=" + collection.variantCount + " keepInstancing=" + instancing);
    }
    static string Key(ShaderCompilerData data, Pass pass) {
        return string.Join(" ", data.shaderKeywordSet.GetShaderKeywords()
                                    .Select(k => k.name)
                                    .Where(k => pass.names.Contains(k))
                                    .OrderBy(k => k, StringComparer.Ordinal));
    }
    public void OnProcessShader(Shader shader, ShaderSnippetData snippet,
                                IList<ShaderCompilerData> data) {
        string path = AssetDatabase.GetAssetPath(shader);
        Dictionary<string, Pass> passes;
        if (!cache.TryGetValue(path, out passes)) {
            if (!File.Exists(path))
                return;
            string source = File.ReadAllText(path);
            if (!IsRecoveredSource(source))
                return;
            passes = ReadPasses(source).ToDictionary(p => p.identity);
            cache.Add(path, passes);
        }
        Pass pass;
        if (!passes.TryGetValue(snippet.passType + "/" + snippet.passName, out pass))
            throw new BuildFailedException("Unknown original pass " + snippet.passType + "/" +
                                           snippet.passName + " in " + path);
        int before = data.Count;
        for (int i = data.Count - 1; i >= 0; i--)
            if (!pass.allowed.Contains(Key(data[i], pass)))
                data.RemoveAt(i);
        var seen = new HashSet<string>(data.Select(d => Key(d, pass)));
        var reachable =
            pass.allowed.Where(k => !k.Split(' ').Contains("UNITY_SINGLE_PASS_STEREO")).ToArray();
        var missing = reachable.Where(k => !seen.Contains(k)).ToArray();
        Debug.Log("NATIVE_ORIGINAL_VARIANTS " + path + " pass=" + pass.identity +
                  " stage=" + snippet.shaderType + " input=" + before + " kept=" + data.Count +
                  " legal=" + pass.allowed.Count + " reachable=" + reachable.Length +
                  " missing=" + missing.Length);
        if (missing.Length > 0)
            throw new BuildFailedException("Reachable original variants were stripped: " + path +
                                           " " + pass.identity + " " +
                                           string.Join("; ", missing));
    }
}
