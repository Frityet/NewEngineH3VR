using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using FistVR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed class RetailThermalWitness : MonoBehaviour
    {
        private const BindingFlags ScopeFields = BindingFlags.NonPublic | BindingFlags.Instance;
        private static bool initialized;

        public static void Initialize()
        {
            string output = Environment.GetEnvironmentVariable("NATIVEH3VR_THERMAL_WITNESS");
            if (initialized || string.IsNullOrEmpty(output)) return;
            initialized = true;
            var host = new GameObject("NativeH3VR retail thermal witness");
            DontDestroyOnLoad(host);
            host.AddComponent<RetailThermalWitness>().StartCoroutine(Run(output));
        }

        private static IEnumerator Run(string output)
        {
            Directory.CreateDirectory(output);
            float deadline = Time.realtimeSinceStartup + 120;
            const string item = "HandheldThermalCam";
            while (Time.realtimeSinceStartup < deadline &&
                (ManagerSingleton<IM>.Instance == null || IM.OD == null || !IM.OD.ContainsKey(item)))
                yield return null;
            if (ManagerSingleton<IM>.Instance == null || IM.OD == null || !IM.OD.ContainsKey(item))
                throw new InvalidOperationException("The retail thermal item was unavailable.");

            var loading = IM.OD[item].GetGameObjectAsync();
            while (!loading.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!loading.IsCompleted || !loading.Result)
                throw new InvalidOperationException("The retail thermal prefab failed to load.");
            var root = new GameObject("Controlled thermal witness");
            root.transform.position = new Vector3(0, 100, 40);
            root.SetActive(false);
            var hand = Instantiate(loading.Result, root.transform, false);
            hand.SetActive(true);
            var scope = hand.GetComponentInChildren<PIPScope>(true);
            if (!scope || !scope.enableThermal || scope.enableNightvision)
                throw new InvalidOperationException("Retail handheld sensor was not configured for thermal rendering.");
            scope.thermalNoise = 0;
            scope.thermalRefreshRate = 0;
            scope.enabled = false;
            root.SetActive(true);
            yield return null;
            // The stock eye camera releases off-screen optic targets each frame.
            // Capture in the same frame as enabling this isolated test optic.
            scope.enabled = true;

            var camera = scope.scopeCam;
            var sensor = camera.targetTexture;
            var display = (RenderTexture)typeof(PIPScope).GetField("rt", ScopeFields).GetValue(scope);
            if (!sensor || !display || sensor == display || !scope.lensRenderer)
                throw new InvalidOperationException("The original scope did not wire distinct sensor and display targets.");
            var source = hand.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials)
                .First(material => material && material.shader &&
                    (material.shader.name == "Alloy/Core" || material.shader.name == "NativeH3VR/Stock PBR") &&
                    material.GetTag("RenderType", false) == "Opaque");

            camera.enabled = false;
            camera.cullingMask = 1 << 31;
            camera.useOcclusionCulling = false;
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.aspect = 1.5f;
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 10;
            camera.transform.position = root.transform.position;
            camera.transform.rotation = Quaternion.identity;
            var bridge = camera.GetComponent<PIPScope_Camera>();
            if (!bridge) throw new InvalidOperationException("The original PIP callbacks were unavailable.");

            CreateTarget(root.transform, source, new Vector3(-.65f, 0, 3), .1f);
            CreateTarget(root.transform, source, new Vector3(.65f, 0, 3), 2);
            var lightObject = new GameObject("Controlled white light");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.rotation = Quaternion.Euler(25, 15, 0);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << 31;

            bridge.isThermal = false;
            scope.SetCameraReplacementShader(camera, false);
            camera.backgroundColor = Color.black;
            camera.clearFlags = CameraClearFlags.Color;
            camera.Render();
            Capture(sensor, output, "ordinary");

            bridge.isThermal = true;
            scope.SetCameraReplacementShader(camera, true);
            PIPScope.PrepareThermalRendering();
            camera.Render();
            Capture(sensor, output, "sensor");
            CaptureHalf(sensor, output, "sensor");
            typeof(PIPScope).GetField("appliedPostProcess", ScopeFields).SetValue(scope, false);
            scope.ApplyScopePostProcess();
            Capture(display, output, "lens");
            CaptureHalf(display, output, "lens");

            bridge.isThermal = false;
            scope.SetCameraReplacementShader(camera, false);
            camera.backgroundColor = Color.black;
            camera.clearFlags = CameraClearFlags.Color;
            camera.Render();
            Capture(sensor, output, "reset");
            File.WriteAllText(Path.Combine(output, "thermal.json"), JsonConvert.SerializeObject(new
            {
                item,
                prefab = loading.Result.name,
                scene = SceneManager.GetActiveScene().name,
                unity = Application.unityVersion,
                device = SystemInfo.graphicsDeviceType.ToString(),
                camera = camera.name,
                lens = scope.lensRenderer.name,
                lensShader = scope.lensRenderer.sharedMaterial.shader.name,
                targetMaterial = source.name,
                targetShader = source.shader.name,
                sensor = new { sensor.width, sensor.height, format = sensor.format.ToString() },
                display = new { display.width, display.height, format = display.format.ToString() }
            }, Formatting.Indented));
            Destroy(root);
            Application.Quit();
        }

        private static void CreateTarget(Transform parent, Material material, Vector3 position, float heat)
        {
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.layer = 31;
            target.transform.SetParent(parent, false);
            target.transform.localPosition = position;
            target.transform.localScale = new Vector3(.85f, .85f, .85f);
            var renderer = target.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            var properties = new MaterialPropertyBlock();
            properties.SetFloat("_ThermalIntensity", heat);
            renderer.SetPropertyBlock(properties);
        }

        private static void Capture(RenderTexture source, string output, string name)
        {
            RenderTexture prior = RenderTexture.active;
            Texture2D pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), pixels.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = prior;
                Destroy(pixels);
            }
        }

        private static void CaptureHalf(RenderTexture source, string output, string name)
        {
            RenderTexture prior = RenderTexture.active;
            Texture2D pixels = new Texture2D(source.width, source.height, TextureFormat.RGBAHalf, false, true);
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".rgba16f"), pixels.GetRawTextureData());
            }
            finally
            {
                RenderTexture.active = prior;
                Destroy(pixels);
            }
        }
    }
}
