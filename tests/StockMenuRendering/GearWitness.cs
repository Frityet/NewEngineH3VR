using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NativeH3VR.QA
{
    // This helper also compiles in the isolated original Unity 5.6 player.
    // The cloned renderers use stock geometry and materials at common world poses.
    sealed class StockIndexGearWitness : IDisposable
    {
        const string BumpMap = "_BumpMap";
        readonly GameObject root;
        readonly List<Material> materials = new List<Material>();
        readonly List<Renderer> hidden = new List<Renderer>();
        readonly Dictionary<Material, Texture> savedNormals = new Dictionary<Material, Texture>();
        readonly List<string> evidence = new List<string>();
        readonly Dictionary<Material, Vector4> savedRim = new Dictionary<Material, Vector4>();
        Texture2D neutral;
        readonly Vector3 viewpointDelta;

        internal StockIndexGearWitness() : this(Vector3.zero) { }

        internal StockIndexGearWitness(Vector3 offsetFromWindowsReference)
        {
            viewpointDelta = offsetFromWindowsReference;
            root = new GameObject("Fixed stock IndexWire witness");
            try {
                var source = FindOriginalIndexMeshes();
                foreach (Renderer original in source) {
                    var mesh = original.GetComponent<MeshFilter>().sharedMesh;
                    var holder = original.transform.parent;
                    // Two orientations let one capture cover corresponding bright
                    // and dim portions of the opposing stock mesh surfaces.
                    AddCopy(original, mesh, holder, new Vector3(-.155f, 2.787f, -7.52f),
                        Quaternion.Euler(15f, 28f, 0f), "near");
                    AddCopy(original, mesh, holder, new Vector3(.155f, 2.787f, -7.52f),
                        Quaternion.Euler(-28f, -22f, 30f), "turned");
                }

                var background = GameObject.CreatePrimitive(PrimitiveType.Cube);
                background.name = "Stock IndexWire matte neutral backdrop";
                background.transform.SetParent(root.transform);
                background.transform.position = new Vector3(0f, 2.70f, -7.19f);
                background.transform.localScale = new Vector3(2.65f, 2.65f, .015f);
                Object.Destroy(background.GetComponent<Collider>());
                var black = new Material(Shader.Find("Unlit/Color"));
                black.SetVector("_Color", new Vector4(.034f, .034f, .034f, 1f));
                background.GetComponent<Renderer>().sharedMaterial = black;
                materials.Add(black);

                neutral = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
                neutral.name = "Diagnostic neutral AG normal";
                neutral.SetPixel(0, 0, new Color(1f, .5f, 1f, .5f));
                neutral.Apply(false, true);
                root.transform.position = viewpointDelta;

                foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>()) {
                    if (renderer.gameObject.scene != SceneManager.GetActiveScene()
                        || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    // Remove only the independently posed tracked hands for
                    // the witness. The near matte panel covers the remaining scene.
                    Transform ancestor = renderer.transform;
                    while (ancestor != null && !ancestor.gameObject.CompareTag("GameController"))
                        ancestor = ancestor.parent;
                    if (ancestor == null) continue;
                    hidden.Add(renderer);
                    renderer.enabled = false;
                }
            } catch {
                Dispose();
                throw;
            }
        }

        static List<Renderer> FindOriginalIndexMeshes()
        {
            var result = new List<Renderer>();
            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>()) {
                if (!renderer || renderer.gameObject.scene != SceneManager.GetActiveScene()
                    || !renderer.gameObject.activeInHierarchy) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var name = filter.sharedMesh.name;
                if (name == "IndexWire_Left_A" || name == "IndexWire_Left_B")
                    result.Add(renderer);
            }
            if (result.Count != 2)
                throw new InvalidOperationException("Expected the two active original left IndexWire meshes. Found " + result.Count);
            return result;
        }

        void AddCopy(Renderer original, Mesh mesh, Transform holder, Vector3 placement,
            Quaternion rotation, string pose)
        {
            var go = new GameObject(mesh.name + " " + pose);
            go.transform.SetParent(root.transform);
            go.transform.position = placement + rotation * holder.InverseTransformPoint(original.transform.position);
            go.transform.rotation = rotation * Quaternion.Inverse(holder.rotation) * original.transform.rotation;
            go.transform.localScale = original.transform.localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            Material material = new Material(original.sharedMaterial);
            renderer.sharedMaterial = material;
            materials.Add(material);
            Texture fromAsset = material.GetTexture(BumpMap);
            savedNormals.Add(material, fromAsset);
            savedRim.Add(material, material.GetVector("_RimColor"));

            string origin = fromAsset == null ? "(null, shader default)"
                : fromAsset.name + " (" + fromAsset.width + "x" + fromAsset.height + ")";
            evidence.Add(string.Join("\t", new[] {
                mesh.name, mesh.vertexCount.ToString(CultureInfo.InvariantCulture), pose, original.sharedMaterial.name,
                material.shader.name, material.GetVector("_RimColor").ToString("F7"),
                origin, go.transform.position.ToString("F7"), go.transform.rotation.ToString("F7"),
                go.transform.localScale.ToString("F7")
            }));
        }

        // Live uses the original stored default on Windows and the active
        // production binding on macOS. Explicit samples enforce the same AG
        // tangent-space vector regardless of platform defaults.
        internal void SetNormal(string normal)
        {
            foreach (var pair in savedNormals) {
                Texture normalMap = pair.Value;
                if (normal == "ag") normalMap = neutral;
                else if (normal == "null") normalMap = null;
                pair.Key.SetTexture(BumpMap, normalMap);
            }
        }

        internal void SetLegacyColor(bool originalUniformColor)
        {
            foreach (var pair in savedRim) {
                Vector4 color = pair.Value;
                if (originalUniformColor) {
                    color.x = Mathf.GammaToLinearSpace(color.x);
                    color.y = Mathf.GammaToLinearSpace(color.y);
                    color.z = Mathf.GammaToLinearSpace(color.z);
                }
                pair.Key.SetVector("_RimColor", color);
            }
        }

        internal void RecordColorSetter(string filename)
        {
            const string property = "_RimColor";
            var records = new List<string>();
            foreach (var pair in savedRim)
            {
                // Same literal used by the stock spawn-lock quickbelt branch.
                var input = new Color(.3f, .3f, 1f, 1f);
                pair.Key.SetColor(property, input);
                var stored = pair.Key.GetVector(property);
                pair.Key.SetVector(property, pair.Value);
                records.Add(pair.Key.name + "\t" + input.ToString("F7") + "\t" + stored.ToString("F7"));
            }
            File.WriteAllLines(filename, records.ToArray());
        }

        internal void SaveEvidence(string filename)
        {
            evidence.Insert(0, "mesh\tvertices\tpose\tmaterial\tshader\trim\tstock_normal\tposition\trotation\tscale");
            evidence.Add("# Native viewpoint delta from original Windows reference	" + viewpointDelta.ToString("F7"));
            File.WriteAllLines(filename, evidence.ToArray());
        }

        public void Dispose()
        {
            foreach (var renderer in hidden) if (renderer != null) renderer.enabled = true;
            foreach (var material in materials) Object.Destroy(material);
            if (neutral != null) Object.Destroy(neutral);
            if (root != null) Object.Destroy(root);
        }
    }
}
// Native-only diagnostic entry point.
namespace NativeH3VR.QA
{
    using UnityEngine.Rendering.PostProcessing;

    public sealed partial class StockMenuAudit
    {
        IEnumerator GearWitness(Camera camera, PostProcessLayer layer, PostProcessVolume cc, PostProcessVolume bloom)
        {
            bool gradeEnabled = cc.enabled;
            bool bloomEnabled = bloom.enabled;
            // The Windows fixture has a 1.6 m eye height. Use the same
            // object-to-eye pose even with the native rig's measured offset.
            var originalHead = new Vector3(0f, 2.6999993f, -8f);
            using (var witness = new StockIndexGearWitness(camera.transform.position - originalHead)) {
                witness.SaveEvidence(Path.Combine(output, "gear-witness.tsv"));
                try {
                    cc.enabled = true;
                    foreach (string normal in new[] { "live", "ag", "null" }) {
                        witness.SetNormal(normal);
                        bloom.enabled = true;
                        yield return Capture("gear-" + normal + "-bloom");
                        bloom.enabled = false;
                        yield return Capture("gear-" + normal + "-no-bloom");
                    }
                    // This is an explicit experiment on pre-migration input.
                    // Normal stock captures already use the imported color.
                    if (Environment.GetEnvironmentVariable("NATIVEH3VR_STOCK_GEAR_LEGACY_CONTROL") == "1")
                    {
                        witness.SetNormal("ag");
                        witness.SetLegacyColor(true);
                        bloom.enabled = true;
                        yield return Capture("gear-legacy-ag-bloom");
                        bloom.enabled = false;
                        yield return Capture("gear-legacy-ag-no-bloom");
                    }
                    witness.RecordColorSetter(Path.Combine(output, "gear-color-setter.tsv"));
                } finally {
                    cc.enabled = gradeEnabled;
                    bloom.enabled = bloomEnabled;
                }
            }
        }
    }
}
