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
    // Runs unchanged against the original Windows Unity 5.6 player. It reads
    // shipping material references and authoring fields, without spawning
    // game-controlled objects or running their behaviors.
    sealed class StockEmissionWitness : IDisposable
    {
        const string EmissionColor = "_EmissionColor";
        internal const string StockTag = "NativeH3VRLegacyHdrColor";
        const int Inputs = 7;
        enum Setter { Named, MainColor, PropertyBlock }

        sealed class Source
        {
            public string Name;
            public Material Material;
            public Color[] Colors;
            public string Property;
            public Setter Api;
        }

        readonly List<Source> sources = new List<Source>();
        readonly List<Renderer> cells = new List<Renderer>();
        readonly List<Color> directLinearReadback = new List<Color>();
        readonly List<Material> captured = new List<Material>();
        readonly List<Renderer> hidden = new List<Renderer>();
        readonly List<string> report = new List<string>();
        readonly GameObject carry;
        readonly Mesh sphere;
        GameObject board;

        internal StockEmissionWitness()
        {
            sphere = StockQuickbeltWitness.FindStockSlot().GetComponent<MeshFilter>().sharedMesh;
            carry = new GameObject("Stock emission reference assets");
            Object.DontDestroyOnLoad(carry);
            carry.SetActive(false);
            carry.AddComponent<MeshFilter>().sharedMesh = sphere;
        }

        internal IEnumerator ReadItemMaterials()
        {
            var im = StockType("FistVR.IM");
            var objects = Member(im, null, "OD") as IDictionary;
            if (objects == null) throw new InvalidOperationException("Stock object registry is unavailable.");
            foreach (string id in new[] { "CompoundPistol", "AmmoCounter_Picatinny", "StudioLight" })
            {
                var definition = objects[id];
                if (definition == null) throw new InvalidOperationException("Unresolved stock emission item " + id);
                var request = definition.GetType().GetMethod("GetGameObjectAsync").Invoke(definition, null);
                yield return request;
                var prefab = Member(request.GetType(), request, "Result") as GameObject;
                if (prefab == null) throw new InvalidOperationException("Stock item request did not provide a prefab: " + id);

                if (id == "CompoundPistol")
                {
                    var owner = prefab.GetComponentInChildren(StockType("FistVR.CompoundPistolPowerSystem"), true);
                    var source = Field<Renderer>(owner, "Rend").sharedMaterial;
                    var grad = Field<Gradient>(owner, "colorGrad");
                    Add("Compound heat", source, new[] { grad.Evaluate(0f), grad.Evaluate(.5f), grad.Evaluate(1f) });
                }
                else if (id == "AmmoCounter_Picatinny")
                {
                    var owner = prefab.GetComponentInChildren(StockType("FistVR.AmmoCounterAttachment"), true);
                    var source = Field<Renderer>(owner, "Rend1").sharedMaterial;
                    var colors = Field<IList>(owner, "Colors");
                    if (colors.Count < 3) throw new InvalidOperationException("Missing authored ammo colors.");
                    Add("Ammo digits", source, new[] { (Color)colors[0], (Color)colors[colors.Count / 2], (Color)colors[colors.Count - 1] });
                }
                else
                {
                    var owner = prefab.GetComponentInChildren(StockType("FistVR.StudioLight"), true);
                    var source = Field<Renderer>(owner, "GlowRend").sharedMaterial;
                    var authored = Field<Color>(owner, "LightColor");
                    var values = new[] { authored, Color.Lerp(Color.black, authored, .5f), authored * 1.25f };
                    Add("Studio light SetColor", source, values, "_Color");
                    Add("Studio light material.color", source, values, "_Color", Setter.MainColor);
                }
            }
        }

        internal void ReadStockScene()
        {
            switch (SceneManager.GetActiveScene().name)
            {
                case "TakeAndHoldClassic":
                    var reticle = CurrentSceneComponent("FistVR.TAH_Reticle");
                    var colors = HealthRamp(reticle);
                    Material main = null;
                    foreach (Material m in Field<IList>(reticle, "HealthMaterials"))
                    {
                        bool night = m.name.IndexOf("_NV", StringComparison.OrdinalIgnoreCase) >= 0;
                        Add(night ? "T&H reticle NV" : "T&H reticle", m, colors);
                        if (!night) main = m;
                    }
                    Add("T&H reticle MPB control", main, colors, EmissionColor, Setter.PropertyBlock);
                    break;
                case "ReturnOfTheRotwieners":
                    var gastro = CurrentSceneComponent("FistVR.ZosigGastroCycler");
                    Add("Gastro health", Field<Renderer>(gastro, "HealthRingRend").sharedMaterial, HealthRamp(gastro));
                    var ending = CurrentSceneComponent("FistVR.ZosigEndingManager");
                    // The original ending sends intensity 0, idle 1, and
                    // a talking range of [1, 5] to all three RGB channels.
                    Add("Ending screen", Field<Renderer>(ending, "FinalScreen").sharedMaterial,
                        new[] { new Color(0f, 0f, 0f, 1f), Color.white, new Color(3f, 3f, 3f, 1f) });
                    break;
                case "WinterWasteland":
                    var weather = CurrentSceneComponent("FistVR.WW_TeleportMaster");
                    var low = Field<Color>(weather, "C_Snowbluster_MinIntensity");
                    var high = Field<Color>(weather, "C_Snowbluster_MaxIntensity");
                    Add("Snowbluster tint", Field<Material>(weather, "MTL_Snowbluster"),
                        new[] { low, Color.Lerp(low, high, .5f), high }, "_TintColor");
                    break;
                default: throw new InvalidOperationException("Unrecognized stock emission reference scene.");
            }
        }

        static Color[] HealthRamp(Component owner)
        {
            Color empty = Field<Color>(owner, "Health_Empty");
            Color full = Field<Color>(owner, "Health_Full");
            return new[] { empty, Color.Lerp(empty, full, .5f), full };
        }

        static Component CurrentSceneComponent(string type)
        {
            var expected = StockType(type);
            foreach (var obj in Resources.FindObjectsOfTypeAll(expected))
            {
                var component = (Component)obj;
                if (component.gameObject.scene == SceneManager.GetActiveScene()) return component;
            }
            throw new InvalidOperationException("Missing original stock renderer owner: " + type);
        }

        static Type StockType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            throw new InvalidOperationException("Missing stock type: " + name);
        }

        static object Member(Type type, object owner, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var field = type.GetField(name, flags);
            if (field != null) return field.GetValue(owner);
            var property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(owner, null);
            throw new InvalidOperationException("Unresolved stock source member: " + type.FullName + "." + name);
        }

        static T Field<T>(Component owner, string name)
        {
            if (owner == null) throw new InvalidOperationException("Missing stock source for " + name);
            return (T)Member(owner.GetType(), owner, name);
        }

        void Add(string label, Material raw, Color[] stocks, string property = EmissionColor, Setter api = Setter.Named)
        {
            if (raw == null || raw.shader == null || !raw.HasProperty(property))
                throw new InvalidOperationException("Wrong stock HDR material: " + label);
            if (property == EmissionColor && !raw.IsKeywordEnabled("_EMISSION"))
                throw new InvalidOperationException("Missing original emission keyword: " + label + " " + raw.name);
            var copy = new Material(raw);
            captured.Add(copy);
            var pin = new GameObject(label);
            pin.transform.SetParent(carry.transform, false);
            pin.AddComponent<MeshRenderer>().sharedMaterial = copy;
            var colors = new[] { stocks[0], stocks[1], stocks[2], Color.black, Color.red,
                new Color(.3f, .6f, .9f, .65f), new Color(.1f, 1.2f, 1.55f, .3f) };
            sources.Add(new Source { Name = label, Material = copy, Colors = colors, Property = property, Api = api });
            report.Add("material\t" + label + "\t" + raw.name + "\t" + raw.shader.name + "\t" +
                string.Join(",", raw.shaderKeywords) + "\t" + property + "\t" + api);
        }

        internal void Arrange(Vector3 offset)
        {
            if (sources.Count != 10)
                throw new InvalidOperationException("Expected ten actual stock HDR material and API rows. Got " + sources.Count);
            board = new GameObject("Stock emission color controls");
            for (int y = 0; y < sources.Count; y++)
            for (int x = 0; x < Inputs; x++)
            {
                var source = sources[y];
                var cell = new GameObject(source.Name + " sample " + x);
                cell.transform.SetParent(board.transform);
                cell.transform.position = new Vector3((x - 3) * .083f, 2.917f - y * .05f, -7.58f) + offset;
                cell.transform.localScale = Vector3.one * .044f;
                cell.AddComponent<MeshFilter>().sharedMesh = sphere;
                var renderer = cell.AddComponent<MeshRenderer>();
                renderer.receiveShadows = false;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                var material = new Material(source.Material);
                // Normalize masks and dim gain at the test material boundary
                // to prevent HDR clipping. Keep the actual shader and variant.
                if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", Texture2D.whiteTexture);
                if (material.HasProperty("_IncandescenceMap")) material.SetTexture("_IncandescenceMap", Texture2D.whiteTexture);
                if (material.HasProperty("_EmissionWeight")) material.SetFloat("_EmissionWeight", .08f);
                if (source.Property == EmissionColor && material.HasProperty("_Color"))
                    material.SetColor("_Color", Color.black);
                if (source.Property == "_TintColor")
                    material.SetTexture("_MainTex", Texture2D.whiteTexture);
                if (source.Api == Setter.PropertyBlock)
                    material.SetColor(source.Property, Color.black);
                renderer.sharedMaterial = material;
                cells.Add(renderer);
                captured.Add(material);
                report.Add("input\t" + source.Name + "\t" + x.ToString(CultureInfo.InvariantCulture) + "\t" +
                    source.Colors[x].ToString("F8") + "\t" + source.Colors[x].linear.ToString("F8") + "\t" +
                    cell.transform.position.ToString("F8"));
            }
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.transform.SetParent(board.transform);
            panel.transform.position = new Vector3(0, 2.7f, -7.18f) + offset;
            panel.transform.localScale = new Vector3(2.65f, 2.65f, .015f);
            Object.Destroy(panel.GetComponent<Collider>());
            var mat = new Material(Shader.Find("Unlit/Color"));
            mat.SetVector("_Color", new Vector4(.034f, .034f, .034f, 1f));
            panel.GetComponent<Renderer>().sharedMaterial = mat;
            captured.Add(mat);
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.gameObject.scene != SceneManager.GetActiveScene()) continue;
                Transform ancestor = r.transform;
                while (ancestor != null && !ancestor.gameObject.CompareTag("GameController")) ancestor = ancestor.parent;
                if (ancestor == null) continue;
                r.enabled = false;
                hidden.Add(r);
            }
            report.Add("# Control: original stock shaders/materials, cloned with test white emission masks and .08 emission weight; preserved stock keywords.");
        }

        internal void SetState(bool linear, Action<Material, string, Color> stockSetter = null)
        {
            string phase = stockSetter != null ? "game" : linear ? "linear" : "raw";
            for (int y = 0; y < sources.Count; y++)
            for (int x = 0; x < Inputs; x++)
            {
                var source = sources[y];
                Color input = linear ? source.Colors[x].linear : source.Colors[x];
                var renderer = cells[y * Inputs + x];
                var material = renderer.sharedMaterial;
                if (source.Api == Setter.PropertyBlock)
                {
                    var block = new MaterialPropertyBlock();
                    block.SetColor(Shader.PropertyToID(source.Property), input);
                    renderer.SetPropertyBlock(block);
                }
                else if (source.Api == Setter.MainColor)
                    material.color = input;
                else if (stockSetter != null)
                {
                    if (material.GetTag(StockTag, false, "") != source.Property)
                        throw new InvalidOperationException("Missing imported stock HDR Material tag: " + source.Name);
                    stockSetter(material, source.Property, input);
                }
                else
                    material.SetColor(source.Property, input);
                var readback = material.GetColor(source.Property);
                if (linear) directLinearReadback.Add(readback);
                if (stockSetter != null && source.Api == Setter.Named && !readback.Equals(directLinearReadback[y * Inputs + x]))
                    throw new InvalidOperationException("Runtime stock HDR setter differs from the confirmed linear input: " +
                        source.Name + " " + readback.ToString("F8"));
                report.Add("readback\t" + phase + "\t" + source.Name + "\t" + x + "\t" +
                    readback.ToString("F8") + "\t" + (source.Property == "_Color" ? material.color.ToString("F8") : "-"));
            }
        }

        internal Material CopySource(string label) =>
            new Material(sources.Find(source => source.Name == label).Material);

        internal void AddRuntimeEvidence(string evidence) => report.Add("# " + evidence);

        internal void Save(string path) => File.WriteAllLines(path, report.ToArray());

        public void Dispose()
        {
            foreach (var r in hidden) if (r != null) r.enabled = true;
            foreach (var m in captured) if (m != null) Object.Destroy(m);
            if (board != null) Object.Destroy(board);
            if (carry != null) Object.Destroy(carry);
        }
    }
}

// Native-only audit entry point.
namespace NativeH3VR.QA
{
    public sealed partial class StockMenuAudit
    {
        IEnumerator EmissionWitness()
        {
            using (var witness = new StockEmissionWitness())
            {
                yield return witness.ReadItemMaterials();
                foreach (string scene in new[] { "TakeAndHoldClassic", "ReturnOfTheRotwieners", "WinterWasteland" })
                {
                    yield return LoadEmissionScene(scene);
                    witness.ReadStockScene();
                }
                yield return LoadEmissionScene("MainMenu3");
                var settings = FistVR.GM.CurrentSceneSettings;
                settings.Vol_CC.enabled = false;
                settings.Vol_Bloom.enabled = false;
                settings.Vol_AO.enabled = false;

                var originalHead = new Vector3(0f, 2.6999993f, -8f);
                witness.Arrange(stereo.SourceCamera.transform.position - originalHead);
                witness.SetState(false);
                yield return Capture("emission-stock-input");
                witness.SetState(true);
                yield return Capture("emission-linear-input");
                var stockSetter = ActualStockEmissionSetter(witness);
                if (stockSetter != null)
                {
                    witness.SetState(false, stockSetter);
                    yield return Capture("emission-game-setter-input");
                }
                witness.Save(Path.Combine(output, "emission-witness.tsv"));
            }
        }

        static Action<Material, string, Color> ActualStockEmissionSetter(StockEmissionWitness witness)
        {
            var reticle = typeof(FistVR.TAH_Reticle);
            var helper = reticle.GetMethod("SetStockHdrMaterialColor", BindingFlags.Static | BindingFlags.NonPublic);
            if (helper == null)
            {
                if (Environment.GetEnvironmentVariable("NATIVEH3VR_REQUIRE_COMPILED_EMISSION") == "1")
                    throw new InvalidOperationException("Compiled stock HDR Material setter was not loaded.");
                return null;
            }
            var assembly = reticle.Assembly;
            var targets = new Dictionary<string, int>
            {
                { "FistVR.WW_StandaloneWeatherSystem::FXManagement", 1 },
                { "FistVR.WW_TeleportMaster::FXManagement", 1 },
                { "FistVR.AmmoCounterAttachment::UpdateDisplay", 1 },
                { "FistVR.StudioLight::UpdateLight", 1 },
                { "FistVR.CompoundPistolPowerSystem::Update", 1 },
                { "FistVR.LAPD2019::UpdateElectricalSystem", 2 },
                { "FistVR.LAPD2019Battery::Update", 1 },
                { "FistVR.OmniCleric::Update", 1 },
                { "FistVR.OmniDisc::Damage", 1 },
                { "FistVR.OmniShape::Init", 1 },
                { "FistVR.TAH_Reticle::Update", 1 },
                { "FistVR.ZosigEndingManager::InitPhase", 1 },
                { "FistVR.ZosigEndingManager::Update", 2 },
                { "FistVR.ZosigGastroCycler::UpdateHealth", 1 },
                { "FistVR.ZosigNPCSpeechInput::Awake", 1 },
                { "FistVR.ZosigNPCSpeechInput::Update", 2 },
                { "MedigunBeam::SetLineColor", 1 }
            };
            foreach (var target in targets)
            {
                var parts = target.Key.Split(new[] { "::" }, StringSplitOptions.None);
                var owner = assembly.GetType(parts[0], true);
                var method = owner.GetMethod(parts[1], BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method == null) throw new InvalidOperationException("Missing compiled HDR caller " + target.Key);
                byte[] body = method.GetMethodBody().GetILAsByteArray();
                var offsets = new List<string>();
                for (int i = 0; i + 5 <= body.Length; i++)
                    if (body[i] == 0x28 && BitConverter.ToInt32(body, i + 1) == helper.MetadataToken)
                        offsets.Add(i.ToString(CultureInfo.InvariantCulture));
                if (offsets.Count != target.Value)
                    throw new InvalidOperationException("Wrong compiled stock HDR calls in " + target.Key);
                witness.AddRuntimeEvidence("runtime IL " + target.Key + " -> " + helper.Name +
                    " at offsets " + string.Join(",", offsets.ToArray()));
            }
            var apply = (Action<Material, string, Color>)Delegate.CreateDelegate(typeof(Action<Material, string, Color>), helper);
            var untagged = witness.CopySource("Studio light SetColor");
            var ordinary = witness.CopySource("Ending screen");
            try
            {
                const string property = "_Color";
                untagged.SetOverrideTag(StockEmissionWitness.StockTag, "");
                ordinary.SetOverrideTag(StockEmissionWitness.StockTag, property);
                string untaggedReadback = CheckStockColorBypass(apply, untagged, property, false, true);
                string ordinaryReadback = CheckStockColorBypass(apply, ordinary, property, true, false);
                witness.AddRuntimeEvidence("runtime stock guard: fresh untagged HDR Material " + untaggedReadback +
                    " and explicitly tagged ordinary Color " + ordinaryReadback + " match their direct RGBA.");
            }
            finally
            {
                Object.Destroy(untagged);
                Object.Destroy(ordinary);
            }
            return apply;
        }

        static string CheckStockColorBypass(Action<Material, string, Color> apply, Material material,
            string property, bool tagged, bool hdr)
        {
            int index = material.shader.FindPropertyIndex(property);
            if (index < 0 || material.shader.GetPropertyType(index) != ShaderPropertyType.Color ||
                ((material.shader.GetPropertyFlags(index) & ShaderPropertyFlags.HDR) != 0) != hdr ||
                (material.GetTag(StockEmissionWitness.StockTag, false, "") == property) != tagged)
                throw new InvalidOperationException("Unexpected stock color guard control for " + material.shader.name);
            var value = new Color(.3f, .6f, 1.2f, .41f);
            material.SetColor(property, value);
            var baseline = material.GetColor(property);
            material.SetColor(property, Color.black);
            apply(material, property, value);
            var actual = material.GetColor(property);
            if (!actual.Equals(baseline))
                throw new InvalidOperationException("Material guard differs from its direct setter in " + material.shader.name +
                    " baseline " + baseline.ToString("F8") + " actual " + actual.ToString("F8"));
            return actual.ToString("F8");
        }

        IEnumerator LoadEmissionScene(string scene)
        {
            SteamVR_LoadLevel.Begin(scene);
            float deadline = Time.realtimeSinceStartup + 100f;
            do
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new TimeoutException("Loading original stock emission scene " + scene);
                yield return null;
            } while (SceneManager.GetActiveScene().name != scene || SteamVR_LoadLevel.loading ||
                     !FistVR.GM.CurrentPlayerBody || stereo.SourceCamera != FistVR.GM.CurrentPlayerBody.EyeCam);
            yield return new WaitForSecondsRealtime(2);
        }
    }
}
