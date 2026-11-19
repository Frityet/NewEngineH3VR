using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NativeH3VR.QA
{
    // External, opt-in test package. Both captures come from the renderer that submits to OpenXR.
    public sealed partial class StockMenuAudit : MonoBehaviour
    {
        [Serializable] sealed class ShaderUse { public string name; public int slots; public bool supported; }
        [Serializable] sealed class Eye {
            public int width, height; public string format; public bool srgb;
            public Vector3 position; public Quaternion rotation; public float[] frustumRadians;
        }
        [Serializable] sealed class Probe {
            public string name, mode, texture, format; public int width, mipCount;
            public float intensity; public bool boxProjection, enabled;
        }
        [Serializable] sealed class Report {
            public string scene, graphics, colorSpace, path, quality, aa, foregroundFade, backgroundFade;
            public int msaa, rendererCount, enabledRendererCount, layerMask;
            public float gridAlpha, reflectionIntensity, ambientIntensity;
            public bool hdr, eyeBound, layerEnabled, compute;
            public Vector3 headPosition; public Quaternion headRotation;
            [NonSerialized] public Eye[] eyes;
            [NonSerialized] public ShaderUse[] shaderUse;
            [NonSerialized] public Probe[] reflectionProbes;
            public string[] volumes, resources, lights, issues, terrainDetails;
        }

        string output;
        NativeStereoRenderer stereo;
        [DllImport("libSystem.B.dylib")]
        static extern int setenv(string name, string value, int overwrite);
        public static void Initialize()
        {
            var go = new GameObject("Stock menu render audit");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<StockMenuAudit>();
        }

        IEnumerator Start()
        {
            output = Environment.GetEnvironmentVariable("NATIVEH3VR_STOCK_MENU_AUDIT");
            if (string.IsNullOrEmpty(output)) yield break;
            var stack = new Stack<IEnumerator>();
            stack.Push(Run());
            while (stack.Count > 0) {
                object next;
                try {
                    var current = stack.Peek();
                    if (!current.MoveNext()) {
                        (stack.Pop() as IDisposable)?.Dispose();
                        continue;
                    }
                    next = current.Current;
                    if (next is IEnumerator child && !(next is CustomYieldInstruction)) {
                        stack.Push(child);
                        continue;
                    }
                } catch (Exception ex) {
                    while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                    File.WriteAllText(Path.Combine(output, "failed.txt"), ex.ToString());
                    Debug.LogError("NATIVE_STOCK_MENU_AUDIT_FAILED " + ex);
                    Application.Quit(2);
                    yield break;
                }
                yield return next;
            }
        }

        IEnumerator Run()
        {
            float deadline = Time.realtimeSinceStartup + 115;
            do {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for live MainMenu3 stereo");
                yield return null;
                stereo = NativeStereoRenderer.Instance;
            } while (SceneManager.GetActiveScene().name != "MainMenu3" || stereo == null ||
                     stereo.SourceCamera == null || GM.CurrentPlayerBody == null || GM.CurrentSceneSettings == null ||
                     stereo.SourceCamera != GM.CurrentPlayerBody.EyeCam || stereo.CompletedFrames < 12);
            // Let the stock introduction fade and shader warm-up finish before fixing the comparison pose.
            yield return new WaitForSecondsRealtime(4);
            var scene = GM.CurrentSceneSettings;
            var camera = stereo.SourceCamera;
            var layer = GM.CurrentPlayerBody.PostLayer;
            var cc = scene.Vol_CC;
            var bloom = scene.Vol_Bloom;
            var ao = scene.Vol_AO;
            SaveReport("menu", Inspect(camera, layer));
            if (cc == null || bloom == null || ao == null || layer == null)
                throw new InvalidDataException("Missing stock post-processing control. See menu.json");
            bool aoEnabled = ao.enabled, ccEnabled = cc.enabled, bloomEnabled = bloom.enabled;
            bool layerEnabled = layer.enabled;
            float originalTime = Time.timeScale;
            Time.timeScale = 0;
            GameObject chart = null;
            try {
                setenv("VR_OPENXR_CAPTURE_EVERY", "1", 1);
                yield return Capture("stock");
                SaveCompositor("stock");
                SaveGradingState(layer);
                yield return Capture("stock-repeat");
                cc.enabled = false;
                yield return Capture("stock-no-grade");
                cc.enabled = ccEnabled;
                bloom.enabled = false;
                yield return Capture("stock-no-bloom");
                bloom.enabled = bloomEnabled;
                ao.enabled = !aoEnabled;
                yield return Capture("stock-toggle-ao");
                ao.enabled = aoEnabled;
                layer.enabled = false;
                yield return Capture("stock-no-post");
                layer.enabled = layerEnabled;
                yield return Capture("stock-restored");
                if (Environment.GetEnvironmentVariable("NATIVEH3VR_STOCK_GEAR_AUDIT") == "1")
                    yield return GearWitness(camera, layer, cc, bloom);
                chart = Chart(camera);
                yield return Capture("chart");
                bloom.enabled = false;
                yield return Capture("chart-no-bloom");
                cc.enabled = false;
                setenv("VR_OPENXR_CAPTURE_EVERY", "1", 1);
                yield return Capture("chart-no-bloom-no-grade");
                SaveCompositor("chart-no-bloom-no-grade");
            } finally {
                setenv("VR_OPENXR_CAPTURE_EVERY", "0", 1);
                cc.enabled = ccEnabled; bloom.enabled = bloomEnabled; ao.enabled = aoEnabled;
                layer.enabled = layerEnabled; Time.timeScale = originalTime;
                if (chart != null) {
                    foreach (var renderer in chart.GetComponentsInChildren<Renderer>()) Destroy(renderer.sharedMaterial);
                    Destroy(chart);
                }
            }
            var surveys = Environment.GetEnvironmentVariable("NATIVEH3VR_STOCK_AUDIT_SCENES") ?? "";
            foreach (string name in surveys.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (!Application.CanStreamedLevelBeLoaded(name)) throw new ArgumentException("Unavailable stock scene " + name);
                SteamVR_LoadLevel.Begin(name);
                deadline = Time.realtimeSinceStartup + 90;
                do {
                    if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Loading stock scene " + name);
                    yield return null;
                    stereo = NativeStereoRenderer.Instance;
                } while (SceneManager.GetActiveScene().name != name || SteamVR_LoadLevel.loading || !GM.CurrentPlayerBody
                         || stereo == null || !stereo.SourceCamera || stereo.SourceCamera != GM.CurrentPlayerBody.EyeCam);
                yield return new WaitForSecondsRealtime(1.5f);
                SaveReport("survey-" + name, Inspect(stereo.SourceCamera, GM.CurrentPlayerBody.PostLayer));
                yield return Capture("survey-" + name);
            }
            File.WriteAllText(Path.Combine(output, "complete"), "MainMenu3\n" + surveys + "\n" + stereo.CompletedFrames);
            Debug.Log("NATIVE_STOCK_MENU_AUDIT_PASS frames=" + stereo.CompletedFrames);
            Application.Quit(0);
        }

        void SaveCompositor(string sample)
        {
            setenv("VR_OPENXR_CAPTURE_EVERY", "0", 1);
            File.Copy(Path.Combine(output, "compositor.png"), Path.Combine(output, sample + "-compositor.png"));
        }

        void SaveReport(string name, Report report)
        {
            // External DLLs do not have a player-built Unity type tree for nested custom arrays.
            string summary = JsonUtility.ToJson(report, true).TrimEnd();
            string Encode<T>(IEnumerable<T> values) => "[" + string.Join(",", values.Select(x => JsonUtility.ToJson(x))) + "]";
            File.WriteAllText(Path.Combine(output, name + ".json"), summary.Substring(0, summary.Length - 1)
                + ",\n\"eyes\":" + Encode(report.eyes) + ",\n\"shaderUse\":" + Encode(report.shaderUse)
                + ",\n\"reflectionProbes\":" + Encode(report.reflectionProbes) + "\n}");
        }

        void SaveGradingState(PostProcessLayer layer)
        {
            const BindingFlags internalField = BindingFlags.Instance | BindingFlags.NonPublic;
            var bundles = (IDictionary)typeof(PostProcessLayer).GetField("m_Bundles", internalField).GetValue(layer);
            var bundle = (PostProcessBundle)bundles[typeof(ColorGrading)];
            var renderer = typeof(PostProcessBundle).GetProperty("renderer", internalField).GetValue(bundle, null);
            var lut = (RenderTexture)renderer.GetType().GetField("m_InternalLogLut", internalField).GetValue(renderer);
            if (lut == null) throw new InvalidDataException("No 3D HDR grading LUT on the stock primary eye");
            var curves = (Texture2D)renderer.GetType().GetField("m_GradingCurves", internalField).GetValue(renderer);
            var context = new List<string> { "lut=" + lut.width + "x" + lut.height + "x" + lut.volumeDepth + " " + lut.graphicsFormat,
                "curve=" + curves.width + "x" + curves.height + " " + curves.graphicsFormat,
                "blended=" + DescribeEffect(bundle.settings, true) };
            var request = AsyncGPUReadback.Request(lut, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            if (request.hasError) throw new IOException("Reading the actual Metal HDR grading lookup table failed");
            using (var file = new BinaryWriter(File.Create(Path.Combine(output, "lut-f32le.rgba")))) {
                for (int layerIndex = 0; layerIndex < request.layerCount; layerIndex++) {
                    var pixels = request.GetData<Color>(layerIndex);
                    foreach (var color in pixels) {
                        file.Write(color.r); file.Write(color.g); file.Write(color.b); file.Write(color.a);
                    }
                }
                if (file.BaseStream.Position != 16L * lut.width * lut.height * lut.volumeDepth)
                    throw new InvalidDataException("Incomplete HDR lookup table readback");
            }
            foreach (int x in new[] { 0, 32, 64, 96, 127 })
                context.Add("curves[" + x + ",0]=" + curves.GetPixel(x, 0).ToString("F6"));
            File.WriteAllLines(Path.Combine(output, "grading.txt"), context.ToArray());
        }

        IEnumerator Capture(string name)
        {
            ulong start = stereo.CompletedFrames;
            for (int i = 0; i < 600 && stereo.CompletedFrames < start + 8; i++) yield return null;
            if (stereo.CompletedFrames < start + 8 || stereo.SuspendSceneRendering)
                throw new InvalidOperationException("No submitted stereo frames for " + name);
            yield return new WaitForEndOfFrame();
            var previous = RenderTexture.active;
            try {
                for (int i = 0; i < 2; i++) {
                    var eye = stereo.EyeTexture(i);
                    RenderTexture.active = eye;
                    var copy = new Texture2D(eye.width, eye.height, TextureFormat.RGB24, false);
                    try {
                        copy.ReadPixels(new Rect(0, 0, eye.width, eye.height), 0, 0, false);
                        copy.Apply(false, false);
                        var path = Path.Combine(output, name + (i == 0 ? "-left.png" : "-right.png"));
                        File.WriteAllBytes(path, copy.EncodeToPNG());
                    } finally { Destroy(copy); }
                }
            } finally { RenderTexture.active = previous; }
        }

        Report Inspect(Camera camera, PostProcessLayer layer)
        {
            var current = SceneManager.GetActiveScene();
            var renderers = Resources.FindObjectsOfTypeAll<Renderer>().Where(r => r.gameObject.scene == current).ToArray();
            var materials = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).ToList();
            if (RenderSettings.skybox != null) materials.Add(RenderSettings.skybox);
            var reference = stereo.TrackingOrigin;
            var frame = stereo.LatestFrame;
            var report = new Report {
                scene = current.name, graphics = SystemInfo.graphicsDeviceName + " " + SystemInfo.graphicsDeviceVersion,
                colorSpace = QualitySettings.activeColorSpace.ToString(), quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                msaa = QualitySettings.antiAliasing, hdr = camera.allowHDR, path = camera.actualRenderingPath.ToString(),
                eyeBound = camera == GM.CurrentPlayerBody.EyeCam, compute = SystemInfo.supportsComputeShaders,
                aa = layer == null ? "" : layer.antialiasingMode.ToString(), layerEnabled = layer != null && layer.enabled,
                layerMask = layer == null ? 0 : layer.volumeLayer.value,
                ambientIntensity = RenderSettings.ambientIntensity, reflectionIntensity = RenderSettings.reflectionIntensity,
                headPosition = reference.TransformPoint(frame.head.Position), headRotation = reference.rotation * frame.head.Rotation,
                foregroundFade = stereo.FadeColor(false).ToString("F4"), backgroundFade = stereo.FadeColor(true).ToString("F4"),
                gridAlpha = stereo.GridAlpha, rendererCount = renderers.Length,
                enabledRendererCount = renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy),
                shaderUse = materials.GroupBy(m => m.shader == null ? "(missing)" : m.shader.name)
                    .Select(g => new ShaderUse { name = g.Key, slots = g.Count(), supported = g.All(m => m.shader != null && m.shader.isSupported) })
                    .OrderByDescending(u => u.slots).ToArray(),
                issues = renderers.SelectMany(r => r.sharedMaterials.Where(m => m == null || m.shader == null || !m.shader.isSupported || m.shader.name == "Hidden/InternalErrorShader")
                    .Select(m => r.name + " [" + r.GetType().Name + ", active=" + (r.enabled && r.gameObject.activeInHierarchy)
                        + ", material=" + (m == null ? "(empty slot)" : m.name)
                        + ", shader=" + (m == null || m.shader == null ? "(none)" : m.shader.name) + "]")).ToArray(),
                reflectionProbes = Resources.FindObjectsOfTypeAll<ReflectionProbe>().Where(p => p.gameObject.scene == current).Select(p => {
                    var t = p.bakedTexture;
                    return new Probe { name = p.name, enabled = p.isActiveAndEnabled, mode = p.mode.ToString(),
                        boxProjection = p.boxProjection, intensity = p.intensity, texture = t == null ? "" : t.name,
                        width = t == null ? 0 : t.width, mipCount = t == null ? 0 : t.mipmapCount,
                        format = t is Cubemap c ? c.graphicsFormat.ToString() : "" };
                }).ToArray(),
                lights = Resources.FindObjectsOfTypeAll<Light>().Where(l => l.gameObject.scene == current)
                    .Select(l => l.name + " " + l.type + " enabled=" + l.isActiveAndEnabled + " power=" + l.intensity + " color=" + l.color).ToArray(),
                volumes = Resources.FindObjectsOfTypeAll<PostProcessVolume>().Where(v => v.gameObject.scene.IsValid())
                    .Select(DescribeVolume).ToArray(),
                resources = StockResources(layer),
                terrainDetails = TerrainDetails(),
                eyes = new Eye[2]
            };
            for (int i = 0; i < 2; i++) {
                var target = stereo.EyeTexture(i);
                var view = i == 0 ? frame.left : frame.right;
                report.eyes[i] = new Eye { width = target.width, height = target.height, format = target.graphicsFormat.ToString(),
                    srgb = target.sRGB, position = reference.TransformPoint(view.pose.Position), rotation = reference.rotation * view.pose.Rotation,
                    frustumRadians = new[] { view.left, view.right, view.up, view.down } };
            }
            return report;
        }

        static string DescribeVolume(PostProcessVolume v)
        {
            var settings = new List<string>();
            if (v.sharedProfile != null)
                settings.AddRange(v.profile.settings.Select(effect => DescribeEffect(effect, false)));
            return v.name + " enabled=" + v.isActiveAndEnabled + " weight=" + v.weight + " " + string.Join(" ", settings);
        }

        static string DescribeEffect(PostProcessEffectSettings effect, bool includeDefaults)
        {
            var values = new List<string>();
            foreach (var member in effect.GetType().GetFields()) {
                if (!(member.GetValue(effect) is ParameterOverride parameter)) continue;
                var field = parameter.GetType().GetField("value");
                if (field == null || !(includeDefaults || parameter.overrideState || member.Name == "enabled")) continue;
                var value = field.GetValue(parameter);
                values.Add(member.Name + "=" + (value is Object obj ? obj.name : Convert.ToString(value, CultureInfo.InvariantCulture)));
            }
            return effect.GetType().Name + "(" + string.Join(",", values) + ")";
        }

        static string[] StockResources(PostProcessLayer layer)
        {
            if (layer == null) return Array.Empty<string>();
            var resources = (PostProcessResources)typeof(PostProcessLayer).GetField("m_Resources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(layer);
            if (resources == null) return new[] { "No PostProcessResources assigned" };
            var result = new List<string>();
            foreach (var group in new object[] { resources.shaders, resources.computeShaders }) {
                if (group == null) continue;
                foreach (var f in group.GetType().GetFields()) {
                    var asset = f.GetValue(group) as Object;
                    result.Add(f.Name + "=" + (asset == null ? "(missing)" : asset.name) +
                        (asset is Shader shader ? " supported=" + shader.isSupported : ""));
                }
            }
            return result.ToArray();
        }

        static string[] TerrainDetails()
        {
            var terrain = Shader.Find("Alloy/Nature/Terrain/4Splat");
            if (terrain == null) return Array.Empty<string>();
            return new[] { "BaseMapShader", "Details0", "Details1", "Details2", "Tree0" }.Select(key => {
                var shader = terrain.GetDependency(key);
                return key + "=" + (shader == null ? "(missing)" : shader.name + " supported=" + shader.isSupported);
            }).ToArray();
        }

        static GameObject Chart(Camera camera)
        {
            var root = new GameObject("Linear test swatches");
            root.transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * .38f
                + camera.transform.up * .12f, camera.transform.rotation);
            root.transform.localScale = Vector3.one * (.38f / 1.5f);
            var shader = Shader.Find("Unlit/Color") ?? throw new InvalidOperationException("Missing Unlit/Color");
            // Equal-height patches on black isolate bloom spread around the HDR patch.
            Panel(root, shader, "back", new Vector3(0, 0, .015f), new Vector3(1.26f, .54f, .015f), 0);
            float[] levels = { .018f, .18f, .5f, 1, 4 };
            for (int i = 0; i < levels.Length; i++)
                Panel(root, shader, "swatch " + i, new Vector3((i - 2) * .24f, 0, 0), new Vector3(.12f, .12f, .012f), levels[i]);
            return root;
        }

        static void Panel(GameObject parent, Shader shader, string name, Vector3 position, Vector3 size, float linear)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            go.transform.localScale = size;
            var material = new Material(shader);
            // SetVector bypasses inspector sRGB colour conversion for these known shader inputs.
            material.SetVector("_Color", new Vector4(linear, linear, linear, 1));
            go.GetComponent<Renderer>().sharedMaterial = material;
        }
    }
}
