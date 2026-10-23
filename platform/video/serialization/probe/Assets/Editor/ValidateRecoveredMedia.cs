using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using RenderHeads.Media.AVProVideo;
public static class ValidateRecoveredMedia {
    [Serializable]
    public class Leaf {
        public string path, kind, value;
    }
    [Serializable]
    public class Record {
        public string file;
        public Leaf[] leaves;
    }
    [Serializable]
    public class Expected {
        public Record[] records;
    }
    [Serializable]
    public class Report {
        public int components, properties;
        public List<string> errors = new List<string>();
    }
    public static void Validate() {
        var expected =
            JsonUtility.FromJson<Expected>(File.ReadAllText("Assets/Recovered/expected.json"));
        var report = new Report();
        foreach (var r in expected.records) {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(r.file);
            var media = go == null ? null : go.GetComponent<MediaPlayer>();
            if (media == null) {
                report.errors.Add(r.file + ": missing MediaPlayer (gameObject=" +
                                  (go == null ? "null"
                                              : go.name + ", components=" +
                                                    go.GetComponents<Component>().Length) +
                                  ")");
                continue;
            }
            var so = new SerializedObject(media);
            report.components++;
            foreach (var l in r.leaves) {
                var p = so.FindProperty(l.path);
                if (p == null) {
                    report.errors.Add(r.file + ": missing property " + l.path);
                    continue;
                }
                bool ok = false;
                switch (l.kind) {
                case "bool":
                    ok = p.boolValue == (l.value == "1");
                    break;
                case "number":
                    double want = double.Parse(l.value, CultureInfo.InvariantCulture);
                    double got = p.propertyType == SerializedPropertyType.Float ? p.doubleValue
                                                                                : p.longValue;
                    ok = Math.Abs(got - want) < 0.00001;
                    break;
                case "string":
                    ok = p.stringValue == l.value;
                    break;
                case "array":
                    ok = p.isArray && p.arraySize == int.Parse(l.value);
                    break;
                case "reference":
                    ok = string.IsNullOrEmpty(l.value)
                             ? p.objectReferenceValue == null
                             : AssetDatabase.AssetPathToGUID(
                                   AssetDatabase.GetAssetPath(p.objectReferenceValue)) == l.value;
                    break;
                }
                report.properties++;
                if (!ok)
                    report.errors.Add(r.file + ": mismatch " + l.path + " expected " + l.value);
            }
        }
        var text = JsonUtility.ToJson(report, true);
        File.WriteAllText("../../../../build/video/serialization/unity-validation.json", text);
        Debug.Log("AVPRO_RECOVERY_VALIDATION " + text);
        EditorApplication.Exit(report.errors.Count == 0 ? 0 : 1);
    }
}
