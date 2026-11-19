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
    // Compiled unchanged in the isolated Windows Unity 5.6 fixture as well.
    // Use the shipping slot mesh, shader, material and stock setter inputs.
    sealed class StockQuickbeltWitness : IDisposable
    {
        const string RimColor = "_RimColor";
        internal static readonly string[] States = { "idle", "selected", "locked", "hardened", "locked-linear-color", "locked-linear-vector", "locked-raw-vector" };
        readonly GameObject root;
        readonly Color[] gameRimColors;
        readonly Material material;
        readonly Material backdropMaterial;
        readonly Renderer indicator;
        readonly List<Renderer> hidden = new List<Renderer>();
        readonly List<string> evidence = new List<string>();

        internal StockQuickbeltWitness(Vector3 cameraOffset)
        {
            var slotType = FindType();
            gameRimColors = ReadStockColors(slotType);
            var source = FindSlot(slotType);
            var sourceFilter = source.GetComponent<MeshFilter>();
            var stock = source.sharedMaterial;
            root = new GameObject("Stock quickbelt color audit");

            try
            {
                var display = new GameObject("Stock quickbelt selection mesh");
                display.transform.SetParent(root.transform);
                display.transform.position = new Vector3(0f, 2.818f, -7.56f) + cameraOffset;
                display.transform.rotation = Quaternion.Euler(12f, 24f, 0f);
                var bounds = sourceFilter.sharedMesh.bounds;
                float meshDiameter = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (meshDiameter <= 0f) throw new InvalidOperationException("Empty stock quickbelt slot mesh");
                display.transform.localScale = Vector3.one * (.14f / meshDiameter);
                display.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
                var mesh = display.AddComponent<MeshRenderer>();
                mesh.shadowCastingMode = ShadowCastingMode.Off;
                mesh.receiveShadows = false;
                mesh.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mesh.lightProbeUsage = LightProbeUsage.Off;
                material = new Material(stock);
                mesh.sharedMaterial = material;
                indicator = mesh;

                var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                panel.name = "Stock quickbelt charcoal backdrop";
                panel.transform.SetParent(root.transform);
                panel.transform.position = new Vector3(0f, 2.7f, -7.19f) + cameraOffset;
                panel.transform.localScale = new Vector3(2.65f, 2.65f, .015f);
                Object.Destroy(panel.GetComponent<Collider>());
                backdropMaterial = new Material(Shader.Find("Unlit/Color"));
                backdropMaterial.SetVector("_Color", new Vector4(.034f, .034f, .034f, 1f));
                panel.GetComponent<Renderer>().sharedMaterial = backdropMaterial;

                foreach (var existing in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (existing.gameObject.scene != SceneManager.GetActiveScene() ||
                        !existing.enabled || !existing.gameObject.activeInHierarchy) continue;
                    Transform ancestor = existing.transform;
                    while (ancestor != null && !ancestor.gameObject.CompareTag("GameController"))
                        ancestor = ancestor.parent;
                    if (ancestor == null) continue;
                    hidden.Add(existing);
                    existing.enabled = false;
                }

                var normal = stock.GetTexture("_BumpMap");
                Add("stock", source.transform.parent.name, source.name, sourceFilter.sharedMesh.name,
                    sourceFilter.sharedMesh.vertexCount.ToString(CultureInfo.InvariantCulture),
                    stock.name, stock.shader.name, string.Join(",", stock.shaderKeywords),
                    stock.GetFloat("_RimPower").ToString("R", CultureInfo.InvariantCulture),
                    stock.GetFloat("_RimBias").ToString("R", CultureInfo.InvariantCulture),
                    stock.GetFloat("_RimWeight").ToString("R", CultureInfo.InvariantCulture),
                    normal != null ? normal.name : "(null)", bounds.size.ToString("F7"),
                    display.transform.position.ToString("F7"), cameraOffset.ToString("F7"),
                    Mathf.GammaToLinearSpace(.3f).ToString("R", CultureInfo.InvariantCulture));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        static Type FindType()
        {
            Type slotType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                slotType = assembly.GetType("FistVR.FVRQuickBeltSlot", false);
                if (slotType != null) break;
            }
            if (slotType == null) throw new InvalidOperationException("The stock quickbelt component type is missing.");
            return slotType;
        }

        internal static Renderer FindStockSlot() => FindSlot(FindType());

        static Color[] ReadStockColors(Type slotType)
        {
            var update = slotType.GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
            var il = update.GetMethodBody().GetILAsByteArray();
            var colors = new List<Color>();
            for (int at = 0; at + 35 <= il.Length; at++)
            {
                // ldstr, four ldc.r4, newobj Color, callvirt Material.SetColor.
                // Verify the actual tokens before interpreting the game literals.
                if (il[at] != 0x72 || il[at + 5] != 0x22 || il[at + 10] != 0x22 ||
                    il[at + 15] != 0x22 || il[at + 20] != 0x22 || il[at + 25] != 0x73 || il[at + 30] != 0x6f)
                    continue;
                var module = update.Module;
                if (module.ResolveString(BitConverter.ToInt32(il, at + 1)) != RimColor) continue;
                var ctor = module.ResolveMethod(BitConverter.ToInt32(il, at + 26));
                var setter = module.ResolveMethod(BitConverter.ToInt32(il, at + 31));
                if (ctor.DeclaringType != typeof(Color) || ctor.Name != ".ctor" ||
                    setter.DeclaringType != typeof(Material) || setter.Name != "SetColor")
                    throw new InvalidOperationException("Unexpected stock quickbelt setter metadata.");
                colors.Add(new Color(BitConverter.ToSingle(il, at + 6), BitConverter.ToSingle(il, at + 11),
                    BitConverter.ToSingle(il, at + 16), BitConverter.ToSingle(il, at + 21)));
                at += 34;
            }
            if (colors.Count != 4) throw new InvalidOperationException("Expected four stock quickbelt rim setters.");
            return colors.ToArray();
        }

        static Renderer FindSlot(Type slotType)
        {
            var field = slotType.GetField("HoverGeo", BindingFlags.Public | BindingFlags.Instance);
            var shape = slotType.GetField("Shape", BindingFlags.Public | BindingFlags.Instance);
            var type = slotType.GetField("Type", BindingFlags.Public | BindingFlags.Instance);
            var slots = new List<Component>();
            foreach (var asset in Resources.FindObjectsOfTypeAll(slotType))
            {
                var component = (Component)asset;
                if (component.gameObject.scene == SceneManager.GetActiveScene()) slots.Add(component);
            }
            slots.Sort((a, b) => StringComparer.Ordinal.Compare(a.name, b.name));
            foreach (var slot in slots)
            {
                if (Convert.ToInt32(shape.GetValue(slot), CultureInfo.InvariantCulture) != 0 ||
                    Convert.ToInt32(type.GetValue(slot), CultureInfo.InvariantCulture) != 0) continue;
                var hover = field.GetValue(slot) as GameObject;
                if (hover == null) continue;
                var renderer = hover.GetComponent<Renderer>();
                var filter = hover.GetComponent<MeshFilter>();
                if (renderer != null && filter != null && filter.sharedMesh != null &&
                    renderer.sharedMaterial != null && renderer.sharedMaterial.name == "QuickSlotGlow" &&
                    renderer.sharedMaterial.shader.name == "Alloy/Core")
                    return renderer;
            }
            throw new InvalidOperationException("No stock spherical QuickSlotGlow hover mesh. Quickbelt slot candidates: " + slots.Count);
        }

        internal void SetState(string state)
        {
            indicator.enabled = state != "idle";
            if (!indicator.enabled) return;
            var rawControl = new Color(.3f, .3f, 1f, 1f);
            switch (state)
            {
                case "selected":
                    material.SetColor(RimColor, gameRimColors[2]);
                    break;
                case "locked":
                    material.SetColor(RimColor, gameRimColors[0]);
                    break;
                case "hardened":
                    material.SetColor(RimColor, gameRimColors[1]);
                    break;
                case "locked-linear-color":
                    material.SetColor(RimColor, rawControl.linear);
                    break;
                case "locked-linear-vector":
                    material.SetVector(RimColor, new Vector4(rawControl.linear.r, rawControl.linear.g, rawControl.linear.b, rawControl.a));
                    break;
                case "locked-raw-vector":
                    material.SetVector(RimColor, new Vector4(rawControl.r, rawControl.g, rawControl.b, rawControl.a));
                    break;
                default: throw new ArgumentOutOfRangeException("state");
            }
            Add("state", state, material.GetVector(RimColor).ToString("F7"));
        }

        void Add(params string[] row)
        {
            evidence.Add(string.Join("\t", row));
        }

        internal void Save(string path)
        {
            File.WriteAllLines(path, evidence.ToArray());
        }

        public void Dispose()
        {
            foreach (var renderer in hidden) if (renderer != null) renderer.enabled = true;
            if (root != null) Object.Destroy(root);
            if (material != null) Object.Destroy(material);
            if (backdropMaterial != null) Object.Destroy(backdropMaterial);
        }
    }
}

// Native-only audit entry point.
namespace NativeH3VR.QA
{
    using UnityEngine.Rendering.PostProcessing;

    public sealed partial class StockMenuAudit
    {
        IEnumerator QuickbeltWitness(Camera camera, PostProcessVolume cc, PostProcessVolume bloom, PostProcessVolume ao)
        {
            var windowsHead = new Vector3(0f, 2.6999993f, -8f);
            bool previousGrade = cc.enabled, previousBloom = bloom.enabled, previousAo = ao.enabled;
            using (var witness = new StockQuickbeltWitness(camera.transform.position - windowsHead))
            {
                try
                {
                    cc.enabled = false;
                    bloom.enabled = false;
                    ao.enabled = false;
                    foreach (string state in StockQuickbeltWitness.States)
                    {
                        witness.SetState(state);
                        yield return Capture("quickbelt-" + state);
                    }
                    witness.Save(Path.Combine(output, "quickbelt-witness.tsv"));
                }
                finally
                {
                    cc.enabled = previousGrade;
                    bloom.enabled = previousBloom;
                    ao.enabled = previousAo;
                }
            }
        }
    }
}
