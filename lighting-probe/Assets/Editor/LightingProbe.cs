using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class LightingProbe {
    public static void ValidateBinary() {
        var asset = AssetDatabase.LoadAssetAtPath<LightingDataAsset>("Assets/BinaryLinked.asset");
        if (asset == null || !Validity(asset).StartsWith("valid=True"))
            throw new Exception("Binary scene link invalid");
        var scene = new SerializedObject(asset).FindProperty("m_Scene").objectReferenceValue;
        if (scene == null || AssetDatabase.GetAssetPath(scene) != "Assets/Probe.unity")
            throw new Exception("Binary GUID encoding incorrect");
        Debug.Log("LIGHTING_BINARY_LINK_VALIDATED " + Validity(asset));
        EditorApplication.Exit(0);
    }
    public static void ValidateOffline() {
        var report = new StringBuilder();
        int count = 0;
        foreach (string path in Directory.GetFiles("Assets/Offline", "*.asset")) {
            var asset = AssetDatabase.LoadAssetAtPath<LightingDataAsset>(path);
            if (asset == null)
                throw new Exception("Lighting asset failed to load: " + path);
            string state = Validity(asset);
            report.AppendLine(path + " " + state);
            if (!state.StartsWith("valid=True"))
                throw new Exception("Lighting remains invalid: " + path + " " + state);
            var scene = new SerializedObject(asset).FindProperty("m_Scene").objectReferenceValue;
            if (scene == null || AssetDatabase.GetAssetPath(scene) != "Assets/Probe.unity")
                throw new Exception("Wrong scene reference: " + path);
            count++;
        }
        if (count != 46)
            throw new Exception("Expected46lightingassets, got" + count);
        File.WriteAllText(Path.GetFullPath("../build/lighting/offline-validation.txt"),
                          report.ToString());
        Debug.Log("LIGHTING_OFFLINE_VALIDATED count=" + count);
        EditorApplication.Exit(0);
    }
    static string Validity(LightingDataAsset asset) {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return "valid=" +
               typeof(LightingDataAsset).GetProperty("isValid", flags).GetValue(asset, null) +
               " error=" +
               typeof(LightingDataAsset)
                   .GetProperty("validityErrorMessage", flags)
                   .GetValue(asset, null);
    }
    public static void Run() {
        EditorSettings.serializationMode = SerializationMode.ForceText;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        var asset =
            AssetDatabase.LoadAssetAtPath<LightingDataAsset>("Assets/OriginalLighting.asset");
        if (asset == null)
            throw new Exception("Missing lighting asset");
        string output = Path.GetFullPath("../build/lighting");
        var report = new StringBuilder();
        report.AppendLine("BEFORE " + Validity(asset));
        File.WriteAllText(Path.Combine(output, "before.json"),
                          EditorJsonUtility.ToJson(asset, true));
        var so = new SerializedObject(asset);
        var prop = so.GetIterator();
        bool children = true;
        while (prop.Next(children)) {
            report.AppendLine(prop.propertyPath + " : " + prop.propertyType);
            children = false;
        }
        var sceneReference = so.FindProperty("m_Scene");
        if (sceneReference == null)
            sceneReference = so.FindProperty("m_SceneRef");
        if (sceneReference == null)
            throw new Exception("No serialized scene reference: " + report);
        sceneReference.objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Probe.unity");
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        report.AppendLine("AFTER " + Validity(asset));
        report.AppendLine("SCENE_PROPERTY " + sceneReference.propertyPath);
        File.WriteAllText(Path.Combine(output, "after.json"),
                          EditorJsonUtility.ToJson(asset, true));
        File.WriteAllText(Path.Combine(output, "result.txt"), report.ToString());
        File.Copy("Assets/OriginalLighting.asset", Path.Combine(output, "migrated.asset"), true);
        Lightmapping.lightingDataAsset = asset;
        EditorSceneManager.SaveScene(scene);
        Debug.Log("LIGHTING_PROBE " + Validity(asset));
        EditorApplication.Exit(0);
    }
}
