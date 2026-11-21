using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness
    {
        private const string CoverMaterial = "mtl_destructibleBarriers";

        private sealed class MaterialDraw
        {
            internal Renderer renderer;
            internal Material[] original;
            internal int[] slots;
        }

        private static object VectorText(Vector3 point) => new[] { point.x, point.y, point.z };
        private static object ColorText(Color color) => new[] { color.r, color.g, color.b, color.a };
        private static object TextureInfo(Texture texture) => !texture ? null : new {
            texture.name, texture.width, texture.height, dimension = texture.dimension.ToString(),
            format = texture.graphicsFormat.ToString(), texture.mipmapCount
        };

        private static object DescribeDraw(MaterialDraw draw)
        {
            var r = draw.renderer;
            var probes = new List<ReflectionProbeBlendInfo>();
            r.GetClosestReflectionProbes(probes);
            LightProbes.GetInterpolatedProbe(r.probeAnchor ? r.probeAnchor.position : r.bounds.center, r, out var sh);
            Vector3[] axes = { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            var colors = new Color[axes.Length];
            sh.Evaluate(axes, colors);
            var properties = new MaterialPropertyBlock();
            r.GetPropertyBlock(properties);
            return new {
                r.name, center = VectorText(r.bounds.center), size = VectorText(r.bounds.size),
                localRotation = r.transform.rotation.ToString("F6"), layer = r.gameObject.layer,
                r.lightmapIndex, reflectionUsage = r.reflectionProbeUsage.ToString(), lightProbeUsage = r.lightProbeUsage.ToString(),
                probeAnchor = r.probeAnchor ? r.probeAnchor.name : null,
                propertyBlock = !properties.isEmpty, ambient = colors.Select(ColorText),
                reflectionProbes = probes.Where(p => p.probe).Select(p => new {
                    p.probe.name, p.weight, p.probe.intensity, p.probe.boxProjection,
                    p.probe.importance, center = VectorText(p.probe.bounds.center), texture = TextureInfo(p.probe.texture)
                }),
                materials = draw.slots.Select(i => {
                    var m = draw.original[i];
                    return new { m.name, shader = m.shader.name, m.shader.isSupported, m.shaderKeywords,
                        floats = new[] { "_Metal", "_Roughness", "_MainRoughnessSource", "_GlossyReflections", "_Specularity" }
                            .Where(m.HasProperty).ToDictionary(n => n, m.GetFloat),
                        textures = new[] { "_MainTex", "_BumpMap", "_SpecTex" }
                            .Where(m.HasProperty).ToDictionary(n => n, n => TextureInfo(m.GetTexture(n))),
                        tint = m.HasProperty("_Color") ? ColorText(m.GetColor("_Color")) : null };
                })
            };
        }

        private IEnumerator InspectHoldCover(string name)
        {
            var covers = new List<MaterialDraw>();
            foreach (var wall in FindObjectsByType<TNH_DestructibleBarrier>())
                foreach (var r in wall.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    var materials = r.sharedMaterials;
                    var indices = Enumerable.Range(0, materials.Length)
                        .Where(i => materials[i] && materials[i].name.StartsWith(CoverMaterial, StringComparison.Ordinal))
                        .ToArray();
                    if (indices.Length != 0)
                        covers.Add(new MaterialDraw { renderer = r, original = materials, slots = indices });
                }
            Require(covers.Count != 0, "No raised steel cover renderers at this stock Hold");
            var sceneProbes = Resources.FindObjectsOfTypeAll<ReflectionProbe>()
                .Where(p => p.gameObject.scene.IsValid()).ToArray();
            var cullers = FindObjectsByType<MonoBehaviour>().Where(b => b.enabled && b.GetType().Name == "ProbeCullManager").ToArray();
            var probeEnabled = sceneProbes.Select(p => p.enabled).ToArray();
            var smoke = FindObjectsByType<ParticleSystemRenderer>().Where(r => r.enabled).ToArray();
            File.WriteAllText(Path.Combine(output, name + "-materials.json"), JsonConvert.SerializeObject(
                new { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    ambientMode = RenderSettings.ambientMode.ToString(), reflectionIntensity = RenderSettings.reflectionIntensity,
                    skyReflection = TextureInfo(ReflectionProbe.defaultTexture), blendScale = ReflectionProbe.defaultTextureHDRDecodeValues.ToString("F4"),
                    activeProbes = sceneProbes.Select(p => new { p.name, mode = p.mode.ToString(), p.enabled,
                        p.gameObject.activeInHierarchy, p.intensity, p.exposureValue, p.cullingMask,
                        bounds = VectorText(p.bounds.center), size = VectorText(p.bounds.size),
                        texture = TextureInfo(p.texture), baked = TextureInfo(p.bakedTexture), custom = TextureInfo(p.customBakedTexture) }),
                    loadedCubemaps = Resources.FindObjectsOfTypeAll<Cubemap>().Select(TextureInfo),
                    lights = FindObjectsByType<Light>().Where(l => l.enabled).Select(l => new { l.name, type = l.type.ToString(),
                        l.intensity, position = VectorText(l.transform.position), l.range }),
                    covers = covers.Select(DescribeDraw).ToArray() }, Formatting.Indented));
            float previousTime = Time.timeScale;
            var copies = new Dictionary<Material, Material>();
            try
            {
                Time.timeScale = 0;
                foreach (string mode in new[] { "untouched", "original", "active-probes", "inactive-probes", "diffuse", "native-metal", "native-diffuse", "albedo", "mask" })
                {
                    foreach (var r in smoke) if (r) r.enabled = mode == "untouched";
                    bool probeControl = mode == "active-probes" || mode == "inactive-probes";
                    if (probeControl)
                    {
                        foreach (var manager in cullers) manager.enabled = false;
                        foreach (var probe in sceneProbes)
                            if (probe.bakedTexture && probe.gameObject.activeInHierarchy) probe.enabled = mode == "active-probes";
                    }
                    foreach (var cover in covers)
                    {
                        var slots = (Material[])cover.original.Clone();
                        foreach (int index in cover.slots)
                        {
                            Material original = cover.original[index];
                            if (mode == "original" || mode == "untouched" || probeControl) continue;
                            if (!copies.TryGetValue(original, out var copy))
                            {
                                copy = CoverControl(original, mode);
                                copies.Add(original, copy);
                            }
                            slots[index] = copy;
                        }
                        cover.renderer.sharedMaterials = slots;
                    }
                    for (int n = 0; n < 5; n++) yield return null;
                    yield return new WaitForEndOfFrame();
                    if (mode == "active-probes")
                        File.WriteAllText(Path.Combine(output, name + "-probe-control.json"),
                            JsonConvert.SerializeObject(covers.Select(DescribeDraw).ToArray(), Formatting.Indented));
                    for (int eye = 0; eye < 2; eye++)
                    {
                        var previous = RenderTexture.active;
                        var source = stereo.EyeTexture(eye);
                        var read = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
                        try
                        {
                            RenderTexture.active = source;
                            read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                            read.Apply(false, false);
                            File.WriteAllBytes(Path.Combine(output, name + "-" + mode + (eye == 0 ? "-left.png" : "-right.png")),
                                read.EncodeToPNG());
                        }
                        finally { RenderTexture.active = previous; Destroy(read); }
                    }
                    foreach (var cover in covers) cover.renderer.sharedMaterials = cover.original;
                    if (probeControl)
                    {
                        for (int i = 0; i < sceneProbes.Length; i++) sceneProbes[i].enabled = probeEnabled[i];
                        foreach (var manager in cullers) manager.enabled = true;
                    }
                    foreach (var material in copies.Values) Destroy(material);
                    copies.Clear();
                }
            }
            finally
            {
                Time.timeScale = previousTime;
                foreach (var r in smoke) if (r) r.enabled = true;
                for (int i = 0; i < sceneProbes.Length; i++) if (sceneProbes[i]) sceneProbes[i].enabled = probeEnabled[i];
                foreach (var manager in cullers) if (manager) manager.enabled = true;
                foreach (var cover in covers) if (cover.renderer) cover.renderer.sharedMaterials = cover.original;
                foreach (var material in copies.Values) Destroy(material);
            }
        }

        private Material CoverControl(Material source, string mode)
        {
            if (mode == "diffuse")
            {
                var clone = new Material(source);
                clone.SetFloat("_Metal", 0);
                return clone;
            }
            string shader = mode == "mask" || mode == "albedo" ?
                (mode == "mask" ? "Unlit/Color" : "Unlit/Texture") :
                builtIn ? "Standard" : "Universal Render Pipeline/Lit";
            var renderShader = Shader.Find(shader);
            Require(renderShader && renderShader.isSupported, "Missing cover diagnostic shader " + shader);
            var material = new Material(renderShader);
            bool isUrp = shader.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal);
            string tex = isUrp ? "_BaseMap" : "_MainTex";
            string tint = isUrp ? "_BaseColor" : "_Color";
            if (material.HasProperty(tex) && source.HasProperty("_MainTex") && mode != "mask")
            {
                material.SetTexture(tex, source.GetTexture("_MainTex"));
                material.SetTextureScale(tex, source.GetTextureScale("_MainTex"));
                material.SetTextureOffset(tex, source.GetTextureOffset("_MainTex"));
            }
            if (material.HasProperty(tint))
                material.SetColor(tint, mode == "mask" ? Color.green :
                    source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
            if (mode == "native-metal" || mode == "native-diffuse")
            {
                material.SetFloat("_Metallic", mode == "native-metal" ? 1 : 0);
                material.SetFloat(isUrp ? "_Smoothness" : "_Glossiness", 0.65f);
            }
            return material;
        }
    }
}
