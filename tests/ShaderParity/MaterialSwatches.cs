// This probe also compiles against the retail Unity 5.6 player.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed partial class MaterialSwatches : MonoBehaviour
    {
        private const int Columns = 12;
        private const int TilePixels = 48;
        private const int SampleLayer = 31;
        private const int SampleMask = 1 << SampleLayer;
        private const string Prefix = "H3VR shader comparison ";
        private static readonly Vector3 Origin = new Vector3(512f, 512f, 512f);
#if UNITY_6000_0_OR_NEWER
        private AssetBundle candidateBundle;
        private Shader candidateShader;
        private string replacedFamily;
#endif

        [Serializable]
        public sealed class Entry
        {
            public string material, shader, texture, normal, packed, emission, keywords, renderType;
            public int queue, passes, page = -1, tile = -1;
            public bool supported, rendererMaterial, meshMaterial;
            public string[] drawSlots;
        }

        [Serializable]
        public sealed class PageIndex
        {
            public string scene, unity, api, colorSpace;
            public int columns = Columns, tilePixels = TilePixels, pages;
            public int assetAddresses, prefabAssets, rendererCount, boundSlots, missingSlots;
            public string status;
            public Entry[] materials;
            public string[] emptyMeshSlots;
            public string candidateShader;
            public int candidateDraws;
        }

        public static void Initialize()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NATIVEH3VR_SHADER_SWATCHES"))) return;
#if UNITY_6000_0_OR_NEWER
            if (FindAnyObjectByType<MaterialSwatches>()) return;
#else
            if (FindObjectOfType<MaterialSwatches>()) return;
#endif
            var host = new GameObject(Prefix + "host");
            DontDestroyOnLoad(host);
            host.AddComponent<MaterialSwatches>();
        }

        private IEnumerator Start()
        {
            string output = Environment.GetEnvironmentVariable("NATIVEH3VR_SHADER_SWATCHES");
            Directory.CreateDirectory(output);
            var pending = new Stack<IEnumerator>();
            pending.Push(Run(output));
            while (pending.Count != 0)
            {
                var work = pending.Peek();
                object value;
                try
                {
                    if (!work.MoveNext())
                    {
                        pending.Pop();
                        continue;
                    }
                    value = work.Current;
                    if (value is IEnumerator && !(value is CustomYieldInstruction))
                    {
                        pending.Push((IEnumerator)value);
                        continue;
                    }
                }
                catch (Exception error)
                {
                    while (pending.Count != 0)
                    {
                        var unwind = pending.Pop() as IDisposable;
                        if (unwind != null) unwind.Dispose();
                    }
                    File.WriteAllText(Path.Combine(output, "swatches-failed.txt"), error.ToString());
                    Debug.LogException(error);
                    Application.Quit();
                    yield break;
                }
                yield return value;
            }
            File.WriteAllText(Path.Combine(output, "swatches-complete.txt"), Application.unityVersion);
            Application.Quit();
        }

        private IEnumerator Run(string output)
        {
#if UNITY_6000_0_OR_NEWER
            var candidatePath = Environment.GetEnvironmentVariable("NATIVEH3VR_SWATCH_CANDIDATE_BUNDLE");
            if (!string.IsNullOrEmpty(candidatePath))
            {
                replacedFamily = Environment.GetEnvironmentVariable("NATIVEH3VR_SWATCH_REPLACED_FAMILY");
                var name = Environment.GetEnvironmentVariable("NATIVEH3VR_SWATCH_CANDIDATE_NAME");
                candidateBundle = AssetBundle.LoadFromFile(candidatePath);
                if (!candidateBundle) throw new IOException("Could not load candidate shader bundle " + candidatePath);
                foreach (var shader in candidateBundle.LoadAllAssets<Shader>())
                {
                    if (shader.name != name) continue;
                    if (candidateShader) throw new InvalidDataException("Duplicate shader in the candidate bundle: " + name);
                    candidateShader = shader;
                }
                if (!candidateShader || !candidateShader.isSupported || string.IsNullOrEmpty(replacedFamily))
                    throw new InvalidDataException("Missing candidate shader or material family: " + name);
            }
#endif
            string[] selection = (Environment.GetEnvironmentVariable("NATIVEH3VR_SWATCH_SCENES") ?? "MainMenu3")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (selection.Length == 0) throw new ArgumentException("Choose at least one scene");
            bool fullSweep = Array.IndexOf(selection, "*") >= 0;
            bool exactSceneBindings = fullSweep ||
                Environment.GetEnvironmentVariable("NATIVEH3VR_SWATCH_EXACT_SCENES") == "1";
            float deadline = Time.realtimeSinceStartup + 150;
            while (SceneManager.GetActiveScene().name != "MainMenu3")
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for the stock menu");
                yield return null;
            }
            yield return new WaitForSecondsRealtime(3);
            string bundles = Environment.GetEnvironmentVariable("NATIVEH3VR_SWATCH_BUNDLES");
            if (!string.IsNullOrEmpty(bundles)) yield return CaptureBundles(output, bundles);
            if (exactSceneBindings)
            {
                // Keep resource-only assets distinct from actual scene bindings.
                if (fullSweep)
                    yield return Capture("resources", output, Resources.LoadAll<Material>(""), new MaterialBindings());
                for (int index = 0; index < SceneManager.sceneCountInBuildSettings; index++)
                {
                    string name = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(index));
                    if (fullSweep || Array.IndexOf(selection, name) >= 0)
                        yield return CaptureScene(index, output);
                }
                yield break;
            }
            // The narrower mode retains the original loaded-asset survey.
            var resourceMaterials = Resources.LoadAll<Material>("");
            foreach (string scene in selection)
            {
                if (scene != SceneManager.GetActiveScene().name)
                {
                    if (!Application.CanStreamedLevelBeLoaded(scene)) throw new ArgumentException("Unknown scene " + scene);
                    yield return SceneManager.LoadSceneAsync(scene);
                    yield return new WaitForSecondsRealtime(2);
                }
                var bindings = new MaterialBindings();
                foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>()) bindings.Add(renderer);
                yield return Capture(scene, output, Resources.FindObjectsOfTypeAll<Material>(), bindings);
            }
            GC.KeepAlive(resourceMaterials);
        }

        private IEnumerator CaptureScene(int index, string output)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(index);
            string name = Path.GetFileNameWithoutExtension(path);
            Debug.Log("NATIVEH3VR_SWATCH_SCENE_BEGIN " + name);
            if (SceneManager.GetActiveScene().buildIndex != index)
            {
                yield return SceneManager.LoadSceneAsync(index);
                yield return new WaitForSecondsRealtime(1);
            }
            Scene scene = SceneManager.GetSceneByBuildIndex(index);
            var bindings = new MaterialBindings();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                bindings.status = "Scene redirected to " + SceneManager.GetActiveScene().name;
                yield return Capture(name, output, bindings.All, bindings);
                yield break;
            }
            foreach (var root in scene.GetRootGameObjects())
                bindings.AddHierarchy(root);
            yield return Capture(name, output, bindings.All, bindings);
            Debug.Log("NATIVEH3VR_SWATCH_SCENE_COMPLETE " + name);
        }

        private sealed class MaterialBindings
        {
            internal readonly HashSet<Material> All = new HashSet<Material>();
            internal readonly HashSet<Material> Meshes = new HashSet<Material>();
            private readonly Dictionary<string, int> roots = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<Material, List<string>> slots = new Dictionary<Material, List<string>>();
            internal readonly List<string> EmptySlots = new List<string>();
            internal int AssetAddresses, PrefabAssets, RendererCount, BoundSlots, MissingSlots;
            internal string status = "ok";

            internal void AddHierarchy(GameObject root)
            {
                string name = Uri.EscapeDataString(root.name);
                int count;
                roots.TryGetValue(name, out count);
                roots[name] = count + 1;
                AddHierarchy(root.transform, name + "/");
            }

            private void AddHierarchy(Transform node, string path)
            {
                var renderers = node.GetComponents<Renderer>();
                var types = new HashSet<Type>();
                var duplicateTypes = new HashSet<Type>();
                foreach (var renderer in renderers)
                    if (!types.Add(renderer.GetType())) duplicateTypes.Add(renderer.GetType());
                foreach (var renderer in renderers)
                {
                    var type = renderer.GetType();
                    Add(renderer, path != null && !duplicateTypes.Contains(type) ? path + type.FullName : null);
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                var duplicates = new HashSet<string>(StringComparer.Ordinal);
                foreach (Transform child in node)
                    if (!names.Add(child.name)) duplicates.Add(child.name);
                foreach (Transform child in node)
                    AddHierarchy(child, path != null && !duplicates.Contains(child.name)
                        ? path + Uri.EscapeDataString(child.name) + "/" : null);
            }

            internal string[] DrawSlots(Material material)
            {
                List<string> paths;
                if (!slots.TryGetValue(material, out paths)) return null;
                return UniqueSlots(paths);
            }

            internal string[] UniqueSlots(List<string> paths)
            {
                var unique = new List<string>();
                foreach (var path in paths)
                    if (roots[path.Substring(0, path.IndexOf('/'))] == 1) unique.Add(path);
                unique.Sort(StringComparer.Ordinal);
                return unique.ToArray();
            }

            internal bool HasHierarchy { get { return roots.Count != 0; } }

            internal void Add(Renderer renderer, string path = null)
            {
                RendererCount++;
                bool mesh = renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (!material)
                    {
                        MissingSlots++;
                        if (path != null && mesh)
                            EmptySlots.Add(path + "/" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        continue;
                    }
                    BoundSlots++;
                    All.Add(material);
                    if (mesh) Meshes.Add(material);
                    if (path != null && mesh)
                    {
                        List<string> paths;
                        if (!slots.TryGetValue(material, out paths)) slots.Add(material, paths = new List<string>());
                        paths.Add(path + "/" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private static string TextureName(Material material, params string[] candidates)
        {
            foreach (string key in candidates)
            {
                if (!material.HasProperty(key)) continue;
                var texture = material.GetTexture(key);
                if (texture) return texture.name;
            }
            return "";
        }

        private static Entry Describe(Material material, bool assigned, bool mesh)
        {
            var shader = material.shader;
            var keys = material.shaderKeywords;
            Array.Sort(keys, StringComparer.Ordinal);
            return new Entry {
                material = material.name, shader = shader ? shader.name : "", supported = shader && shader.isSupported,
                texture = TextureName(material, "_BaseMap", "_MainTex"), normal = TextureName(material, "_BumpMap"),
                packed = TextureName(material, "_SpecTex", "_MetallicGlossMap", "_SpecGlossMap"),
                emission = TextureName(material, "_EmissionMap"), queue = material.renderQueue,
                passes = material.passCount, renderType = material.GetTag("RenderType", false),
                keywords = string.Join(" ", keys), rendererMaterial = assigned, meshMaterial = mesh
            };
        }

        private static bool IsSurface(Entry item)
        {
            // Lens, sky, post, particle, terrain splat and procedural passes need their actual draw inputs.
            // Record them in the same inventory. A default mesh would give a false black measurement.
            if (item.queue > 2500 || item.queue < 1001 || !item.supported || item.passes == 0) return false;
            string s = item.shader;
            const string thermalInput = "Hidden/NativeH3VR/ThermalInput/";
            if (s == thermalInput + "Universal Render Pipeline/Lit" ||
                s == thermalInput + "Universal Render Pipeline/Simple Lit")
                s = s.Substring(thermalInput.Length);
            if (s.StartsWith("Alloy", StringComparison.Ordinal))
                return !s.Contains("/Particles/") && !s.Contains("/Decal/") && !s.Contains("/Terrain/");
            return s == "Standard" || s == "Standard (Specular setup)" || s == "NativeH3VR/Stock PBR" ||
                   s == "Universal Render Pipeline/Lit" || s == "Universal Render Pipeline/Simple Lit" ||
                   s == "Universal Render Pipeline/Unlit" || s == "Unlit/Color" || s == "Unlit/Texture" ||
                   s == "CynCliff HeightBlend" || s == "Partly Lit/SulfurLitDiffuse" ||
                   s.StartsWith("NatureManufacture Shaders/", StringComparison.Ordinal);
        }

        private IEnumerator Capture(string scene, string output, IEnumerable<Material> candidates, MaterialBindings bindings)
        {
            var loaded = new List<Material>(candidates);
            loaded.Sort(delegate(Material a, Material b) { return string.CompareOrdinal(a.name, b.name); });
            var entries = new List<Entry>();
            var surfaces = new List<Material>();
            var unique = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var material in loaded)
            {
                if (!material) continue;
                var item = Describe(material, bindings.All.Contains(material), bindings.Meshes.Contains(material));
                if (bindings.HasHierarchy && item.rendererMaterial)
                    item.drawSlots = bindings.DrawSlots(material);
                else
                {
                    string identity = JsonConvert.SerializeObject(item);
                    if (unique.ContainsKey(identity)) continue;
                    unique.Add(identity, item);
                }
                entries.Add(item);
                if (!IsSurface(item)) continue;
                item.page = surfaces.Count / (Columns * Columns);
                item.tile = surfaces.Count % (Columns * Columns);
                surfaces.Add(material);
            }

            int pages = (surfaces.Count + Columns * Columns - 1) / (Columns * Columns);
            var index = new PageIndex { scene = scene, api = SystemInfo.graphicsDeviceType.ToString(),
                unity = Application.unityVersion, colorSpace = QualitySettings.activeColorSpace.ToString(),
                pages = pages, materials = entries.ToArray(), status = bindings.status,
                assetAddresses = bindings.AssetAddresses, prefabAssets = bindings.PrefabAssets,
                rendererCount = bindings.RendererCount, boundSlots = bindings.BoundSlots, missingSlots = bindings.MissingSlots,
                emptyMeshSlots = bindings.UniqueSlots(bindings.EmptySlots) };
#if UNITY_6000_0_OR_NEWER
            if (candidateShader)
            {
                index.candidateShader = candidateShader.name;
                foreach (var surface in surfaces)
                    if (surface.shader.name == replacedFamily) index.candidateDraws++;
            }
#endif
            File.WriteAllText(Path.Combine(output, scene + ".json"), JsonConvert.SerializeObject(index));
            if (pages == 0) yield break;

            var root = new GameObject(Prefix + "stage");
            root.transform.position = Origin - Vector3.forward * 8;
            var cam = new GameObject(Prefix + "camera").AddComponent<Camera>();
            cam.transform.SetParent(root.transform);
            cam.transform.localPosition = Vector3.zero;
            cam.orthographic = true;
            cam.orthographicSize = Columns / 2f;
            cam.aspect = 1f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 15f;
            cam.cullingMask = SampleMask;
#if !UNITY_6000_0_OR_NEWER
            cam.useOcclusionCulling = false;
#endif
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.21f, 0.36f, 0.48f, 1f);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            int size = Columns * TilePixels;
            var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            target.Create();
            cam.targetTexture = target;
#if !UNITY_6000_0_OR_NEWER
            // The retail SteamVR runtime resets auxiliary cameras' pose. Keep
            // the offscreen scene's view and culling matrices at the stage.
            cam.worldToCameraMatrix = Matrix4x4.TRS(root.transform.position, Quaternion.identity,
                new Vector3(1f, 1f, -1f)).inverse;
            cam.cullingMatrix = cam.projectionMatrix * cam.worldToCameraMatrix;
#endif
            var oldSun = RenderSettings.sun;
            var ambientMode = RenderSettings.ambientMode;
            var ambientColor = RenderSettings.ambientLight;
            var ambientProbe = RenderSettings.ambientProbe;
            bool fog = RenderSettings.fog;
            float previousTimeScale = Time.timeScale;
            var lights = new Dictionary<Light, int>();
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            primitive.SetActive(false);
            primitive.transform.SetParent(root.transform);
            var mesh = Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            var reflection = new Cubemap(16, TextureFormat.RGBA32, true);
            var oldReflectionMode = RenderSettings.defaultReflectionMode;
#if UNITY_6000_0_OR_NEWER
            var oldReflection = RenderSettings.customReflectionTexture;
#else
            var oldReflection = RenderSettings.customReflection;
#endif
            float oldIntensity = RenderSettings.reflectionIntensity;
#if UNITY_6000_0_OR_NEWER
            List<Material> trialMaterials = null;
#endif
            try
            {
#if UNITY_6000_0_OR_NEWER
                if (candidateShader)
                {
                    trialMaterials = new List<Material>(index.candidateDraws);
                    for (int i = 0; i < surfaces.Count; i++)
                    {
                        var original = surfaces[i];
                        if (original.shader.name != replacedFamily) continue;
                        var trial = new Material(original);
                        trialMaterials.Add(trial);
                        trial.shader = candidateShader;
                        trial.shaderKeywords = original.shaderKeywords;
                        trial.renderQueue = original.rawRenderQueue;
                        surfaces[i] = trial;
                    }
                }
#endif
                Time.timeScale = 0;
#if UNITY_6000_0_OR_NEWER
                var sceneLights = FindObjectsByType<Light>();
#else
                var sceneLights = FindObjectsOfType<Light>();
#endif
                foreach (var light in sceneLights)
                {
                    lights.Add(light, light.cullingMask);
                    light.cullingMask &= ~SampleMask;
                }
                var sun = new GameObject(Prefix + "key").AddComponent<Light>();
                sun.transform.SetParent(root.transform);
                sun.type = LightType.Directional;
                sun.color = Color.white;
                sun.intensity = 1.25f;
                sun.shadows = LightShadows.None;
                sun.cullingMask = SampleMask;
                sun.transform.rotation = Quaternion.LookRotation(new Vector3(0.35f, -0.55f, 1));
                RenderSettings.sun = sun;
                RenderSettings.fog = false;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.gray;
                var sh = new SphericalHarmonicsL2();
                sh.AddAmbientLight(new Color(0.25f, 0.25f, 0.25f, 1));
                RenderSettings.ambientProbe = sh;
                var gray = new Color[16 * 16];
                for (int i = 0; i < gray.Length; i++) gray[i] = new Color(0.6f, 0.6f, 0.6f, 1f);
                for (int i = 0; i < 6; i++) reflection.SetPixels(gray, (CubemapFace)i);
                reflection.Apply(true);
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
#if UNITY_6000_0_OR_NEWER
                RenderSettings.customReflectionTexture = reflection;
#else
                RenderSettings.customReflection = reflection;
#endif
                RenderSettings.reflectionIntensity = 1f;
                mesh.uv2 = mesh.uv;
                var white = new Color32[mesh.vertexCount];
                for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
                mesh.colors32 = white;

                var grid = new MeshRenderer[Columns * Columns];
                for (int i = 0; i < grid.Length; i++)
                {
                    var obj = new GameObject(Prefix + i);
                    obj.transform.SetParent(root.transform);
                    obj.transform.position = Origin + new Vector3(i % Columns - (Columns - 1) / 2f,
                        (Columns - 1) / 2f - i / Columns, 0f);
                    obj.transform.localScale = Vector3.one * 0.91f;
                    obj.layer = SampleLayer;
                    obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = obj.AddComponent<MeshRenderer>();
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    grid[i] = renderer;
                }
                for (int page = 0; page < pages; page++)
                {
                    for (int i = 0; i < grid.Length; i++)
                    {
                        int m = page * grid.Length + i;
                        grid[i].enabled = m < surfaces.Count;
                        if (m < surfaces.Count) grid[i].sharedMaterial = surfaces[m];
                    }
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    Save(target, Path.Combine(output, scene + "-" + page.ToString("D3") + ".png"));
                }
            }
            finally
            {
                foreach (var old in lights) if (old.Key) old.Key.cullingMask = old.Value;
                RenderSettings.sun = oldSun;
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientColor;
                RenderSettings.ambientProbe = ambientProbe;
                RenderSettings.defaultReflectionMode = oldReflectionMode;
#if UNITY_6000_0_OR_NEWER
                RenderSettings.customReflectionTexture = oldReflection;
#else
                RenderSettings.customReflection = oldReflection;
#endif
                RenderSettings.reflectionIntensity = oldIntensity;
                Time.timeScale = previousTimeScale;
                cam.enabled = false;
                cam.targetTexture = null;
                if (RenderTexture.active == target) RenderTexture.active = null;
                Destroy(target);
                Destroy(root);
                Destroy(reflection);
                Destroy(mesh);
#if UNITY_6000_0_OR_NEWER
                if (trialMaterials != null)
                    foreach (var trial in trialMaterials) Destroy(trial);
#endif
            }
            yield return null;
        }

#if UNITY_6000_0_OR_NEWER
        private void OnDestroy()
        {
            if (candidateBundle) candidateBundle.Unload(true);
        }
#endif

        private static void Save(RenderTexture source, string file)
        {
            var old = RenderTexture.active;
            var read = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = source;
                read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                read.Apply();
                File.WriteAllBytes(file, read.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = old;
                Destroy(read);
            }
        }
    }
}
