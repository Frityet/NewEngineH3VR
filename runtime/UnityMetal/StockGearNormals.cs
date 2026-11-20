using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NativeH3VR
{
    internal static class StockGearNormals
    {
        static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
        static readonly HashSet<string> MaterialNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "mtl_glow_blue",
            "mtl_glow_blue_backup",
            "mtl_glow_green",
            "mtl_glow_purple",
            "mtl_glow_White",
            "QuickSlotGlow",
            "QuickSlotGlow_NV",
            "QuickSlotGlowConstant",
            "QuickSlotGlowConstant_NV",
        };

        static Texture2D neutralNormal;

        internal static void Install()
        {
            // Older prepared projects still need the per-material fallback.
            if (!Resources.Load<TextAsset>("NativeH3VR/LegacyNormalDecoding"))
                SceneManager.sceneLoaded += SceneLoaded;
        }

        static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Fix the shared rig and prefab materials before game Start methods
            // instantiate slots or clone the material for the hover colour.
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
                Apply(material);
        }

        internal static void Apply(Material material)
        {
            const string instanceSuffix = " (Instance)";
            string name = material.name;
            int suffix = name.IndexOf(instanceSuffix, StringComparison.Ordinal);
            if (suffix >= 0) name = name.Substring(0, suffix);
            if (!MaterialNames.Contains(name) || material.shader.name != "Alloy/Core" ||
                !material.IsKeywordEnabled("EFFECT_BUMP") || !material.IsKeywordEnabled("_RIM_ON") ||
                !material.HasProperty(BumpMap) || material.GetTexture(BumpMap))
                return;

            // Recovered D3D programs read tangent X from alpha and tangent Y
            // from green. Unity's Metal default supplies X in red. Supply a
            // neutral AG normal so the original rim uses the mesh normal.
            if (!neutralNormal)
            {
                neutralNormal = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true)
                {
                    name = "NativeH3VR neutral gear normal",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                neutralNormal.SetPixel(0, 0, new Color(1, .5f, 1, .5f));
                neutralNormal.Apply(false, true);
            }
            material.SetTexture(BumpMap, neutralNormal);
        }
    }
}
