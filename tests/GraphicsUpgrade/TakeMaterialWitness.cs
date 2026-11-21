using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness
    {
        private IEnumerator InspectTakeMaterials(string scene, string variant, int preset, string[] requested)
        {
            var draws = new List<MaterialDraw>[requested.Length];
            for (int i = 0; i < draws.Length; i++) draws[i] = new List<MaterialDraw>();
            foreach (var renderer in FindObjectsByType<Renderer>())
            {
                if (!renderer.enabled || renderer.gameObject.scene.name != scene) continue;
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < requested.Length; i++)
                {
                    string name = requested[i];
                    bool prefixMatch = name.EndsWith("*", StringComparison.Ordinal);
                    if (prefixMatch) name = name.Substring(0, name.Length - 1);
                    var slots = Enumerable.Range(0, materials.Length).Where(n => materials[n] &&
                        (prefixMatch ? materials[n].name.StartsWith(name, StringComparison.Ordinal) :
                            materials[n].name == name || materials[n].name == name + " (Instance)")).ToArray();
                    if (slots.Length > 0)
                        draws[i].Add(new MaterialDraw { renderer = renderer, original = materials, slots = slots });
                }
            }
            Require(draws.All(group => group.Count != 0), "Could not find all requested Take materials");
            string prefix = "take-lighting-" + variant;
            File.WriteAllText(Path.Combine(output, scene + "-" + prefix + ".json"),
                JsonConvert.SerializeObject(new {
                    lightmaps = LightmapSettings.lightmaps.Select(map => TextureInfo(map.lightmapColor)),
                    groups = requested.Select((name, i) => new { name, index = i, renderers = draws[i].Select(DescribeDraw) })
                }, Formatting.Indented));

            float previousTime = Time.timeScale;
            var mask = CoverControl(draws[0][0].original[draws[0][0].slots[0]], "mask");
            var lightmaps = draws.SelectMany(group => group).Select(draw => draw.renderer)
                .Distinct().ToDictionary(r => r, r => r.lightmapIndex);
            var options = Type.GetType("NativeH3VR.GraphicsUpgrade.GraphicsOptions, NativeH3VR.GraphicsUpgrade", !builtIn);
            var setting = Type.GetType("NativeH3VR.GraphicsUpgrade.GraphicsSetting, NativeH3VR.GraphicsUpgrade", !builtIn);
            var set = options?.GetMethod("SetIndex");
            void Set(string key, int index) => set.Invoke(null, new[] { Enum.Parse(setting, key), (object)index });
            try
            {
                Time.timeScale = 0;
                yield return Sample(scene, prefix + "-original", false);
                for (int i = 0; i < draws.Length; i++)
                {
                    foreach (var draw in draws[i])
                    {
                        var slots = (Material[])draw.original.Clone();
                        foreach (int slot in draw.slots) slots[slot] = mask;
                        draw.renderer.sharedMaterials = slots;
                    }
                    yield return Sample(scene, prefix + "-mask-" + i, false);
                    foreach (var draw in draws[i]) draw.renderer.sharedMaterials = draw.original;
                }
                if (!builtIn)
                {
                    foreach (string key in new[] { "Tonemapping", "Sharpening", "Bloom", "AmbientOcclusion" }) Set(key, 0);
                    Set("Exposure", 4);
                }
                yield return Sample(scene, prefix + "-ungraded", false);
                if (!builtIn)
                {
                    Set("ShadowQuality", 0);
                    yield return Sample(scene, prefix + "-ungraded-no-shadows", false);
                }
                foreach (var renderer in lightmaps.Keys) renderer.lightmapIndex = -1;
                yield return Sample(scene, prefix + "-no-baked-map", false);
            }
            finally
            {
                foreach (var draw in draws.SelectMany(group => group))
                    if (draw.renderer) draw.renderer.sharedMaterials = draw.original;
                foreach (var original in lightmaps)
                    if (original.Key) original.Key.lightmapIndex = original.Value;
                if (!builtIn) Set("Preset", preset);
                Time.timeScale = previousTime;
                Destroy(mask);
            }
        }
    }
}
