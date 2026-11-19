using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NativeH3VR.QA
{
    // Controlled solid-mesh witness for the actual stock Alloy/Core shader
    // and original SystemNode/SystemCore prefab values. It does not activate a
    // game node or its Take and Hold state.
    sealed class StockTnhColorWitness : IDisposable
    {
        static readonly Vector3 WindowsHead = new Vector3(0f, 2.6999993f, -8f);
        readonly GameObject root;
        readonly List<Renderer> hidden = new List<Renderer>();
        readonly List<Material> materials = new List<Material>();
        readonly List<string> evidence = new List<string>();
        readonly Color[] colors;
        Texture2D flatNormal;

        internal StockTnhColorWitness(Camera camera, bool originalWindows)
        {
            Vector3 offset = originalWindows ? Vector3.zero : camera.transform.position - WindowsHead;
            var cyan = new Color(.058823563f, 2.0430028f, 4f, 1f);
            var dying = new Color(4f, .79269785f, .058823586f, 1f);
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(cyan, 0f), new GradientColorKey(dying, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            gradient.mode = GradientMode.Blend;
            colors = new[]
            {
                cyan, new Color(4f, 2.9127789f, .058823586f, 1f), new Color(4f, 1.6081134f, .058823586f, 1f),
                new Color(4f, .6296144f, .058823586f, 1f), new Color(2.764706f, 3.386613f, 4f, 1f),
                cyan, dying, gradient.Evaluate(0f), gradient.Evaluate(.5f), gradient.Evaluate(1f)
            };
            string[] names = {
                "node-passive", "node-hacking", "node-analyzing", "node-identified",
                "core-protected", "core-exposed", "core-dying", "gradient-0", "gradient-50", "gradient-100"
            };

            root = new GameObject("Stock T&H color controls");
            try
            {
                var source = StockQuickbeltWitness.FindStockSlot();
                var stockMaterial = source.sharedMaterial;
                var shader = stockMaterial.shader;
                var sphere = source.GetComponent<MeshFilter>().sharedMesh;
                flatNormal = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
                flatNormal.SetPixel(0, 0, new Color(1f, .5f, 1f, .5f));
                flatNormal.Apply(false, true);
                for (int i = 0; i < colors.Length; i++)
                {
                    var cell = new GameObject(names[i]);
                    cell.transform.SetParent(root.transform);
                    cell.transform.position = new Vector3((i % 5 - 2) * .105f, 2.865f - (i / 5) * .105f, -7.56f) + offset;
                    cell.transform.localScale = Vector3.one * .076f;
                    cell.AddComponent<MeshFilter>().sharedMesh = sphere;
                    var renderer = cell.AddComponent<MeshRenderer>();
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    // This captured real stock material has the same original
                    // Alloy/Core HDR property as the node materials. Its
                    // authored shader switches remain enabled in Unity 5.6.
                    var material = new Material(stockMaterial);
                    material.SetTexture("_BumpMap", flatNormal);
                    renderer.sharedMaterial = material;
                    materials.Add(material);
                    var g = colors[i].linear;
                    evidence.Add(names[i] + "\t" + colors[i].ToString("F8") + "\t" + g.ToString("F8") +
                        "\t" + cell.transform.position.ToString("F8"));
                }

                var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                panel.transform.SetParent(root.transform);
                panel.transform.position = new Vector3(0, 2.7f, -7.19f) + offset;
                panel.transform.localScale = new Vector3(2.65f, 2.65f, .015f);
                Object.Destroy(panel.GetComponent<Collider>());
                var black = new Material(Shader.Find("Unlit/Color"));
                black.SetVector("_Color", new Vector4(.034f, .034f, .034f, 1f));
                panel.GetComponent<Renderer>().sharedMaterial = black;
                materials.Add(black);
                foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy || r.gameObject.scene != SceneManager.GetActiveScene()) continue;
                    Transform ancestor = r.transform;
                    while (ancestor != null && !ancestor.gameObject.CompareTag("GameController")) ancestor = ancestor.parent;
                    if (ancestor == null) continue;
                    r.enabled = false;
                    hidden.Add(r);
                }
                evidence.Add("# controlled " + shader.name + " / source SystemNode+SystemCore NodeRenderer HDR _RimColor; "
                    + "controlled geometry/preset uses the actual stock QuickSlotGlow Sphere and the shared HDR property, with matched neutral AG normal; "
                    + "gradient keys exactly match the original serialized 0/1, alpha=1, Blend");
                evidence.Add("# original sRGB control / Unity native exact linear control");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void SetColors(bool convert)
        {
            for (int i = 0; i < colors.Length; i++)
                materials[i].SetColor("_RimColor", convert ? colors[i].linear : colors[i]);
        }

        internal bool SetColorsThroughGame()
        {
            const string node = "FistVR.TNH_HoldPointSystemNode";
            const string core = "FistVR.TNH_HoldPointSystemCore";
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var coreType = assembly.GetType(core, false);
                if (coreType == null) continue;
                var helper = coreType.GetMethod("SetStockLinearRimColor", BindingFlags.NonPublic | BindingFlags.Static);
                if (helper == null) return false;

                var body = helper.GetMethodBody().GetILAsByteArray();
                if (body.Length != 15 || body[0] != 0x02 || body[1] != 0x03 || body[2] != 0x0f ||
                    body[3] != 2 || body[4] != 0x28 || body[9] != 0x6f || body[14] != 0x2a)
                    throw new InvalidOperationException("Unexpected runtime stock T&H setter helper shape.");
                var getter = helper.Module.ResolveMethod(BitConverter.ToInt32(body, 5));
                var setter = helper.Module.ResolveMethod(BitConverter.ToInt32(body, 10));
                if (getter.DeclaringType != typeof(Color) || getter.Name != "get_linear" ||
                    setter.DeclaringType != typeof(Material) || setter.Name != "SetColor")
                    throw new InvalidOperationException("Unexpected runtime conversion boundary.");

                var nodeType = assembly.GetType(node, true);
                RequireManagedCalls(nodeType, "SetNodeMode", helper, 4);
                RequireManagedCalls(coreType, "SetCoreMode", helper, 3);
                RequireManagedCalls(coreType, "Damage", helper, 1);
                for (int i = 0; i < colors.Length; i++)
                    helper.Invoke(null, new object[] { materials[i], "_RimColor", colors[i] });
                return true;
            }
            throw new InvalidOperationException("Stock T&H core type is missing.");
        }

        void RequireManagedCalls(Type owner, string name, MethodInfo helper, int expected)
        {
            var method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            var il = method.GetMethodBody().GetILAsByteArray();
            var offsets = new List<string>();
            for (int at = 0; at + 5 <= il.Length; at++)
                if (il[at] == 0x28 && BitConverter.ToInt32(il, at + 1) == helper.MetadataToken)
                    offsets.Add(at.ToString(CultureInfo.InvariantCulture));
            if (offsets.Count != expected)
                throw new InvalidOperationException("Expected actual stock T&H calls from " + owner.Name + "." + name);
            evidence.Add("# runtime IL " + owner.Name + "." + name + " -> " + helper.Name +
                " at offsets " + string.Join(",", offsets.ToArray()));
        }

        internal void Save(string path) => File.WriteAllLines(path, evidence.ToArray());

        public void Dispose()
        {
            foreach (var r in hidden) if (r != null) r.enabled = true;
            foreach (var m in materials) if (m != null) Object.Destroy(m);
            if (flatNormal != null) Object.Destroy(flatNormal);
            if (root != null) Object.Destroy(root);
        }
    }
}

// Native-only audit entry point.
namespace NativeH3VR.QA
{
    using UnityEngine.Rendering.PostProcessing;

    public sealed partial class StockMenuAudit
    {
        IEnumerator TnhColorWitness(Camera camera, PostProcessVolume cc, PostProcessVolume bloom, PostProcessVolume ao)
        {
            bool grade = cc.enabled, bloomEnabled = bloom.enabled, aoEnabled = ao.enabled;
            using (var witness = new StockTnhColorWitness(camera, false))
            {
                try
                {
                    cc.enabled = false;
                    bloom.enabled = false;
                    ao.enabled = false;
                    witness.SetColors(false);
                    yield return Capture("tnh-color-stock-input");
                    witness.SetColors(true);
                    yield return Capture("tnh-color-linear-input");
                    if (witness.SetColorsThroughGame()) yield return Capture("tnh-color-game-setter-input");
                    witness.Save(Path.Combine(output, "tnh-color-witness.tsv"));
                }
                finally
                {
                    cc.enabled = grade;
                    bloom.enabled = bloomEnabled;
                    ao.enabled = aoEnabled;
                }
            }
        }
    }
}
