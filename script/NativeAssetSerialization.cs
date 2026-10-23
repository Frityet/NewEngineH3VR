using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class NativeAssetSerialization {
    public static void CompactTextures() {
        // Unity upgrades native texture assets on import. ForceText expands
        // their GPU payloads to hexadecimal YAML; preserve native binary form.
        var paths =
            Directory.GetFiles("Assets", "*.texture2D", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Assets", "*.cubemap", SearchOption.AllDirectories))
                .ToArray();
        long before = paths.Sum(p => new FileInfo(p).Length);
        try {
            EditorSettings.serializationMode = SerializationMode.ForceBinary;
            for (int offset = 0; offset < paths.Length; offset += 128) {
                AssetDatabase.ForceReserializeAssets(
                    paths.Skip(offset).Take(128), ForceReserializeAssetsOptions.ReserializeAssets);
                Debug.Log("NATIVE_TEXTURE_BINARY_COMPACTION " +
                          Math.Min(offset + 128, paths.Length) + "/" + paths.Length);
            }
        } finally {
            // Mixed keeps existing text scenes and native binary textures.
            EditorSettings.serializationMode = SerializationMode.Mixed;
        }
        long after = paths.Sum(p => new FileInfo(p).Length);
        string report = "{\"textures\":" + paths.Length + ",\"beforeBytes\":" + before +
                        ",\"afterBytes\":" + after + "}";
        File.WriteAllText(Path.GetFullPath("../build/texture-binary-compaction.json"), report);
        Debug.Log("NATIVE_TEXTURE_BINARY_COMPACTION_DONE " + report);
        EditorApplication.Exit(0);
    }
}
