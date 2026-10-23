using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEditor.Compilation;

// Read-only preflight. Existing null scripts are accepted only at their exact
// original exported prefab GUID / GameObject fileID / component slot.
public static class NativeAssetIntegrity {
    [Serializable]
    public sealed class Slot {
        public long gameObjectFileID, componentFileID;
        public int componentIndex;
        public string gameObjectName, reason;
    }
    [Serializable]
    public sealed class Prefab {
        public string path, guid;
        public int expectedMissingComponentCount;
        public Slot[] baseline;
    }
    [Serializable]
    public sealed class Baseline {
        public bool passed;
        public int prefabCount;
        public Prefab[] prefabs;
    }
    [Serializable]
    public sealed class Result {
        public int prefabs, components, preservedOriginalMissing, standaloneAssets;
        public string[] errors;
    }

    static Baseline ReadBaseline() {
        var path = Environment.GetEnvironmentVariable("NATIVEH3VR_PREFAB_BASELINE");
        if (string.IsNullOrEmpty(path))
            path = Path.GetFullPath(
                Path.Combine(Application.dataPath,
                             "../../build/scene-integrity/prefab-missing-script-baseline.json"));
        var source = JsonUtility.FromJson<Baseline>(File.ReadAllText(path));
        if (source == null || !source.passed || source.prefabs == null ||
            source.prefabCount != source.prefabs.Length ||
            source.prefabs.Any(p => p.baseline == null ||
                                    p.expectedMissingComponentCount != p.baseline.Length))
            throw new InvalidOperationException("Missing or failed pristine prefab baseline: " +
                                                path);
        return source;
    }

    public static void InspectAllPrefabs(string[] additionalPaths = null) {
        // Drive coverage from the original inventory, not discovery of surviving assets.
        var original = ReadBaseline().prefabs.Select(p => p.path);
        var discovered = AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath);
        InspectPrefabs(original.Concat(discovered)
                           .Concat(additionalPaths ?? new string[0])
                           .Distinct()
                           .ToArray());
    }

    static readonly HashSet<string> checkedScriptPaths =
        new HashSet<string>(StringComparer.Ordinal);
    public static bool IsPluginCompatibleWithPlatform(PluginImporter plugin, BuildTarget target) {
        // The per-platform flag is independent of Any Platform in Unity 2021.
        // An Any Platform plugin is included unless that target is excluded.
        return plugin.GetCompatibleWithAnyPlatform() ? !plugin.GetExcludeFromAnyPlatform(target)
                                                     : plugin.GetCompatibleWithPlatform(target);
    }

    public static void ValidateRuntimeScript(UnityEngine.Object value, string context) {
        MonoScript script =
            value is MonoBehaviour      ? MonoScript.FromMonoBehaviour((MonoBehaviour)value)
            : value is ScriptableObject ? MonoScript.FromScriptableObject((ScriptableObject)value)
                                        : null;
        if (!(value is MonoBehaviour) && !(value is ScriptableObject))
            return;
        if (script == null || script.GetClass() == null)
            throw new InvalidOperationException("Unresolved managed script: " + context + ":" +
                                                value.name);
        var path = AssetDatabase.GetAssetPath(script);
        if (checkedScriptPaths.Contains(path))
            return;
        var plugin = AssetImporter.GetAtPath(path) as PluginImporter;
        if (plugin != null) {
            if (plugin.isNativePlugin ||
                !IsPluginCompatibleWithPlatform(plugin, BuildTarget.StandaloneOSX))
                throw new InvalidOperationException(
                    "Managed script plugin excluded from macOS player: " + path);
        } else {
            var assembly = CompilationPipeline.GetAssemblyNameFromScriptPath(path);
            var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            if (string.IsNullOrEmpty(assembly) ||
                !playerAssemblies.Any(a => a.name == Path.GetFileNameWithoutExtension(assembly)))
                throw new InvalidOperationException("Managed source script excluded from player: " +
                                                    path);
        }
        checkedScriptPaths.Add(path);
    }

    public static void InspectPrefabs(string[] assetPaths) {
        checkedScriptPaths.Clear();
        var source = ReadBaseline();
        var baseline = source.prefabs.ToDictionary(p => p.path, StringComparer.Ordinal);
        var dependencies =
            AssetDatabase.GetDependencies(assetPaths, true).Concat(assetPaths).Distinct().ToArray();
        var targets =
            dependencies.Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        var errors = new List<string>();
        var result = new Result();
        // Missing ScriptableObject classes also appear as "Script attached"
        // warnings, despite not belonging to a prefab or scene GameObject.
        foreach (var asset in dependencies.Where(
                     p => p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))) {
            if (AssetDatabase.LoadMainAssetAtPath(asset) == null)
                errors.Add("Cannot load standalone asset (possibly unresolved ScriptableObject): " +
                           asset);
            foreach (var item in AssetDatabase.LoadAllAssetsAtPath(asset).Where(o => o != null))
                ValidateRuntimeScript(item, asset);
            result.standaloneAssets++;
            if (result.standaloneAssets % 1000 == 0)
                EditorUtility.UnloadUnusedAssetsImmediate();
        }
        foreach (var target in targets) {
            Prefab expected;
            if (!baseline.TryGetValue(target, out expected)) {
                errors.Add("No pristine prefab identity: " + target);
                continue;
            }
            if (AssetDatabase.AssetPathToGUID(target) != expected.guid) {
                errors.Add("Changed prefab GUID: " + target);
                continue;
            }
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(target);
            if (root == null) {
                errors.Add("Cannot load prefab: " + target);
                continue;
            }
            var slots = (expected.baseline ?? new Slot[0])
                            .ToDictionary(s => s.gameObjectFileID + ":" + s.componentIndex,
                                          StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tr in root.GetComponentsInChildren<Transform>(true)) {
                string guid;
                long localId;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tr.gameObject, out guid,
                                                                    out localId)) {
                    errors.Add("Cannot resolve GameObject identity: " + target + ":" + tr.name);
                    continue;
                }
                var components = tr.GetComponents<Component>();
                for (int index = 0; index < components.Length; index++) {
                    result.components++;
                    if (components[index] != null) {
                        ValidateRuntimeScript(components[index], target);
                        continue;
                    }
                    var key = localId + ":" + index;
                    if (guid != expected.guid || !slots.ContainsKey(key) || !seen.Add(key))
                        errors.Add("NEW missing component: " + target + " GameObject=" + localId +
                                   " name=" + tr.name + " slot=" + index);
                    else
                        result.preservedOriginalMissing++;
                }
            }
            foreach (var slot in slots.Keys.Where(key => !seen.Contains(key)))
                errors.Add("Original missing slot changed or disappeared: " + target + " " + slot);
            result.prefabs++;
            if (result.prefabs % 500 == 0) {
                Debug.Log("NATIVEH3VR_PREFAB_AUDIT_PROGRESS " + result.prefabs + "/" +
                          targets.Length);
                EditorUtility.UnloadUnusedAssetsImmediate();
            }
        }
        result.errors = errors.ToArray();
        var report = Path.GetFullPath(Path.Combine(
            Application.dataPath, "../../build/scene-integrity/prefab-editor-inspection.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(report));
        File.WriteAllText(report, JsonUtility.ToJson(result, true));
        Debug.Log("NATIVEH3VR_PREFAB_AUDIT " + JsonUtility.ToJson(result));
        if (errors.Count != 0)
            throw new InvalidOperationException("Prefab integrity failed with " + errors.Count +
                                                " errors; see " + report);
    }

    public sealed class ReferenceExpectation {
        public string property, type, name, localKey;
    }
    public sealed class ObjectExpectation {
        public string key, type, name;
        public int componentSlotCount;
        public int[] nullComponentIndices = new int[0];
        public List<ReferenceExpectation> references = new List<ReferenceExpectation>();
        public string[] shaderCollectionMembers;
    }
    public sealed class AssetExpectation {
        public string sourcePath, mainType;
        public List<ObjectExpectation> objects = new List<ObjectExpectation>();
    }
    static string RuntimeType(UnityEngine.Object item) {
        // Unity's Editor subclass is not part of the player API.
        var type =
            item is RuntimeAnimatorController ? typeof(RuntimeAnimatorController) : item.GetType();
        return type.FullName + ", " + type.Assembly.GetName().Name;
    }
    static bool EditorObject(UnityEngine.Object item) {
        // AnimatorController is an Editor subclass of a real runtime object;
        // its AnimatorState/AnimatorStateMachine authoring subassets are not.
        return !(item is RuntimeAnimatorController) &&
               item.GetType().Assembly.GetName().Name.StartsWith("UnityEditor",
                                                                 StringComparison.Ordinal);
    }
    static bool EditorOnly(UnityEngine.Object item) {
        var go = item as GameObject;
        if (go == null && item is Component)
            go = ((Component)item).gameObject;
        for (var tr = go == null ? null : go.transform; tr != null; tr = tr.parent)
            if (tr.CompareTag("EditorOnly"))
                return true;
        return false;
    }
    static void Hierarchy(GameObject root, string key, Dictionary<UnityEngine.Object, string> map) {
        if (EditorOnly(root))
            return;
        map.Add(root, key);
        var components = root.GetComponents<Component>();
        for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
            if (components[componentIndex] != null)
                map.Add(components[componentIndex], key + "/component:" + componentIndex);
        int childIndex = 0;
        foreach (Transform child in root.transform)
            if (!EditorOnly(child.gameObject))
                Hierarchy(child.gameObject, key + "/child:" + childIndex++, map);
    }
    static bool SkipProperty(string path) {
        // Component/child topology is checked separately; these editor pointers
        // and the MonoScript object itself are not runtime gameplay references.
        return path == "m_Script" || path.StartsWith("m_Component", StringComparison.Ordinal) ||
               path.StartsWith("m_Children", StringComparison.Ordinal) ||
               path.StartsWith("m_Editor", StringComparison.Ordinal) ||
               path == "m_CorrespondingSourceObject" || path == "m_PrefabInstance" ||
               path == "m_PrefabAsset";
    }
    static bool InspectReferences(UnityEngine.Object value) {
        return value is Component || value is GameObject || value is ScriptableObject ||
               value is Material || value is RuntimeAnimatorController || value is AnimationClip ||
               value is ShaderVariantCollection;
    }
    static List<ReferenceExpectation> References(UnityEngine.Object value,
                                                 Dictionary<UnityEngine.Object, string> map) {
        var references = new List<ReferenceExpectation>();
        if (!InspectReferences(value))
            return references;
        // Unity's runtime native map can enumerate SVC shaders in a different
        // order. Its complete semantic membership is checked separately below.
        if (value is ShaderVariantCollection)
            return references;
        if (value is RuntimeAnimatorController) {
            // The Editor's state graph becomes packed runtime controller data.
            // Compare its public runtime clip inventory, not editor-only graph pointers.
            foreach (var clip in ((RuntimeAnimatorController)value).animationClips.Distinct())
                references.Add(new ReferenceExpectation { property = "runtime.animationClips",
                                                          type = RuntimeType(clip),
                                                          name = clip.name });
            return references;
        }
        using (var serialized = new SerializedObject(value)) {
            var property = serialized.GetIterator();
            bool enter = true;
            while (property.Next(enter)) {
                enter = !SkipProperty(property.propertyPath);
                if (!enter)
                    continue;
                // Avoid scanning bulk numeric animation/geometry arrays. Arrays
                // of objects or user structs remain fully traversed.
                if (property.isArray &&
                    new[] { "float", "int", "unsigned int", "UInt8", "SInt8", "Vector2f",
                            "Vector3f", "Vector4f", "ColorRGBA", "Quaternionf", "Matrix4x4f" }
                        .Contains(property.arrayElementType))
                    enter = false;
                if (property.propertyType != SerializedPropertyType.ObjectReference)
                    continue;
                var target = property.objectReferenceValue;
                if (target == null || EditorOnly(target) || EditorObject(target))
                    continue; // Source-authored null fields are valid.
                string localKey;
                map.TryGetValue(target, out localKey);
                references.Add(new ReferenceExpectation { property = property.propertyPath,
                                                          type = RuntimeType(target),
                                                          name = target.name,
                                                          localKey = localKey });
            }
        }
        return references;
    }
    static string EncodeMember(string value) { return value.Length + ":" + value; }
    static string[] ShaderCollectionMembers(ShaderVariantCollection collection) {
        var members = new List<string>();
        using (var serialized = new SerializedObject(collection)) {
            var shaders = serialized.FindProperty("m_Shaders");
            if (shaders == null || !shaders.isArray)
                throw new InvalidOperationException(
                    "Unknown ShaderVariantCollection shader schema: " + collection.name);
            for (int i = 0; i < shaders.arraySize; i++) {
                var entry = shaders.GetArrayElementAtIndex(i);
                var reference = entry.FindPropertyRelative("first");
                var shader = reference == null ? null : reference.objectReferenceValue as Shader;
                var variants = entry.FindPropertyRelative("second.variants");
                if (shader == null || variants == null || !variants.isArray)
                    throw new InvalidOperationException(
                        "Unresolved ShaderVariantCollection shader/variants: " + collection.name);
                // Preserve the existing reference identity contract (type/name).
                // Bundle-local shader GUIDs are zero and pathIDs are remapped.
                string identity = EncodeMember(RuntimeType(shader)) + EncodeMember(shader.name);
                members.Add("shader:" + identity); // Preserve zero-variant entries too.
                for (int j = 0; j < variants.arraySize; j++) {
                    var variant = variants.GetArrayElementAtIndex(j);
                    var pass = variant.FindPropertyRelative("passType");
                    var keywords = variant.FindPropertyRelative("keywords");
                    if (pass == null || pass.propertyType != SerializedPropertyType.Integer ||
                        keywords == null || keywords.propertyType != SerializedPropertyType.String)
                        throw new InvalidOperationException(
                            "Unknown ShaderVariantCollection variant schema: " + collection.name);
                    var tokens = keywords.stringValue
                                     .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                                     .Distinct(StringComparer.Ordinal)
                                     .OrderBy(k => k, StringComparer.Ordinal);
                    members.Add("variant:" + identity + "pass:" + pass.intValue + ":" +
                                string.Concat(tokens.Select(EncodeMember)));
                }
            }
        }
        // Sort, do not deduplicate: missing, duplicate, or unexpected membership fails.
        return members.OrderBy(m => m, StringComparer.Ordinal).ToArray();
    }
    static ObjectExpectation Describe(UnityEngine.Object item, string key,
                                      Dictionary<UnityEngine.Object, string> map) {
        var components =
            item is GameObject ? ((GameObject)item).GetComponents<Component>() : new Component[0];
        return new ObjectExpectation {
            key = key,
            type = RuntimeType(item),
            name = item.name,
            componentSlotCount = components.Length,
            nullComponentIndices =
                Enumerable.Range(0, components.Length).Where(i => components[i] == null).ToArray(),
            shaderCollectionMembers = item is ShaderVariantCollection
                                          ? ShaderCollectionMembers((ShaderVariantCollection)item)
                                          : null,
            references = References(item, map)
        };
    }
    static AssetExpectation Capture(string path, UnityEngine.Object main,
                                    UnityEngine.Object[] all) {
        if (main == null)
            throw new InvalidOperationException("Cannot load main asset: " + path);
        var result = new AssetExpectation { sourcePath = path, mainType = RuntimeType(main) };
        var map = new Dictionary<UnityEngine.Object, string>();
        if (main is GameObject) {
            Hierarchy((GameObject)main, "root", map);
            foreach (var pair in map.OrderBy(p => p.Value, StringComparer.Ordinal))
                result.objects.Add(Describe(pair.Key, pair.Value, map));
            // LoadAssetWithSubAssets can expose additional runtime objects owned
            // by a prefab. Hierarchy objects already have positional identities;
            // retain only genuinely additional objects in the subasset multiset.
            foreach (var item in all
                         .Where(o => o != null && !map.ContainsKey(o) && !EditorOnly(o) &&
                                     !EditorObject(o))
                         .Distinct())
                result.objects.Add(Describe(item, RuntimeType(item) + ":" + item.name, map));
        } else {
            // Treat same-name/type subassets as a multiset of their reference
            // expectations; bundle serialization may reorder local object IDs.
            foreach (var item in all.Concat(new[] { main })
                         .Where(o => o != null && !EditorOnly(o) && !EditorObject(o))
                         .Distinct())
                result.objects.Add(Describe(item, RuntimeType(item) + ":" + item.name, map));
        }
        return result;
    }
    public static AssetExpectation CaptureSource(string path) {
        return Capture(path, AssetDatabase.LoadMainAssetAtPath(path),
                       AssetDatabase.LoadAllAssetsAtPath(path));
    }
    static string ReferenceKey(ReferenceExpectation reference) {
        return reference.property + "|" + reference.type + "|" + reference.name + "|" +
               reference.localKey;
    }
    static bool Matches(ObjectExpectation expected, ObjectExpectation actual) {
        if (expected.key != actual.key || expected.type != actual.type ||
            expected.name != actual.name ||
            actual.componentSlotCount != expected.componentSlotCount ||
            !actual.nullComponentIndices.SequenceEqual(expected.nullComponentIndices))
            return false;
        if ((expected.shaderCollectionMembers == null) !=
                (actual.shaderCollectionMembers == null) ||
            expected.shaderCollectionMembers != null &&
                !expected.shaderCollectionMembers.SequenceEqual(actual.shaderCollectionMembers))
            return false;
        var actualReferences =
            new HashSet<string>(actual.references.Select(ReferenceKey), StringComparer.Ordinal);
        return expected.references.All(r => actualReferences.Contains(ReferenceKey(r)));
    }
    public static int ValidateBundleAsset(AssetExpectation expected, UnityEngine.Object main,
                                          UnityEngine.Object[] all) {
        var actual = Capture(expected.sourcePath, main, all);
        if (actual.mainType != expected.mainType)
            throw new InvalidOperationException("Bundled main asset type changed: " +
                                                expected.sourcePath);
        var unmatched = new List<ObjectExpectation>(actual.objects);
        foreach (var original in expected.objects) {
            var index = unmatched.FindIndex(item => Matches(original, item));
            if (index < 0) {
                var candidate = unmatched.FirstOrDefault(item => item.key == original.key &&
                                                                 item.type == original.type);
                var missing =
                    candidate == null ? "object absent or runtime type/assembly changed"
                    : original.shaderCollectionMembers != null &&
                            (candidate.shaderCollectionMembers == null ||
                             !original.shaderCollectionMembers.SequenceEqual(
                                 candidate.shaderCollectionMembers))
                        ? "ShaderVariantCollection shader/pass/keyword membership changed"
                    : original.componentSlotCount != candidate.componentSlotCount ||
                            !original.nullComponentIndices.SequenceEqual(
                                candidate.nullComponentIndices)
                        ? "component slot layout changed: expected count=" +
                              original.componentSlotCount + " nulls=[" +
                              string.Join(",", original.nullComponentIndices) +
                              "] actual count=" + candidate.componentSlotCount + " nulls=[" +
                              string.Join(",", candidate.nullComponentIndices) + "]"
                        : string.Join(", ", original.references.Select(ReferenceKey)
                                                .Except(candidate.references.Select(ReferenceKey))
                                                .Take(8)
                                                .ToArray());
                throw new InvalidOperationException(
                    "Bundled object/component/subasset or non-null reference changed: " +
                    expected.sourcePath + " " + original.key + " (" + original.type + ") " +
                    missing);
            }
            unmatched.RemoveAt(index);
        }
        if (unmatched.Count != 0)
            throw new InvalidOperationException("Unexpected bundled hierarchy/subassets: " +
                                                expected.sourcePath);
        return expected.objects.Sum(
            o => o.references.Count +
                 (o.shaderCollectionMembers == null ? 0 : o.shaderCollectionMembers.Length));
    }
}
