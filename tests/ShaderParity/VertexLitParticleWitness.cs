// Compiles against both the retail Unity 5.6 player and the Unity 6 player.
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NativeH3VR.QA
{
    public sealed class VertexLitParticleWitness : MonoBehaviour
    {
        private const string SceneName = "BreachAndClear_TestScene1";
        private const int SampleLayer = 31;

        public static void Initialize()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NATIVEH3VR_VERTEX_PARTICLE_WITNESS")))
                return;
            foreach (var witness in Resources.FindObjectsOfTypeAll<VertexLitParticleWitness>())
                if (witness) return;
            var host = new GameObject("Stock vertex-lit particle witness");
            DontDestroyOnLoad(host);
            host.AddComponent<VertexLitParticleWitness>();
        }

        private IEnumerator Start()
        {
            string output = Environment.GetEnvironmentVariable("NATIVEH3VR_VERTEX_PARTICLE_WITNESS");
            Directory.CreateDirectory(output);
            float deadline = Time.realtimeSinceStartup + 60f;
            while (SceneManager.GetActiveScene().name != "MainMenu3")
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Waiting for stock menu");
                yield return null;
            }
            yield return SceneManager.LoadSceneAsync(SceneName);
            yield return new WaitForSecondsRealtime(2f);

            ParticleSystemRenderer original = null;
            foreach (var particle in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
                if (particle.gameObject.scene.name == SceneName && particle.sharedMaterial &&
                    particle.sharedMaterial.name == "Plant01_dust")
                {
                    original = particle;
                    break;
                }
            if (!original) throw new InvalidOperationException("The original Plant01_dust renderer is missing");

            var material = original.sharedMaterial;
            if (Environment.GetEnvironmentVariable("NATIVEH3VR_EXPECT_STOCK_VERTEX_PARTICLES") == "1" &&
                (material.renderQueue != 3000 || material.GetTag("RenderType", false) != "Transparent" ||
                 !material.GetTexture("_MainTex")))
                throw new InvalidOperationException("The legacy particle no longer uses its transparent texture pass");
            var root = new GameObject("Particle parity stage");
            root.transform.position = new Vector3(512, 512, 512);
            var camera = root.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 16f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.28f, 0.46f, 1);
            camera.cullingMask = 1 << SampleLayer;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
#if !UNITY_6000_0_OR_NEWER
            camera.worldToCameraMatrix = Matrix4x4.TRS(root.transform.position, Quaternion.identity,
                new Vector3(1, 1, -1)).inverse;
            camera.cullingMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;
#endif
            var stage = new GameObject("Particle parity emitter") { layer = SampleLayer };
            stage.transform.SetParent(root.transform, false);
            stage.transform.localPosition = Vector3.forward * 5;
            var particles = stage.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.loop = false;
            main.startSpeed = 0;
            main.startLifetime = 60f;
            main.startSize = 1.3f;
            main.startColor = new Color(1, 1, 1, 128f / 255f);
            var emission = particles.emission;
            emission.enabled = false;
            var shape = particles.shape;
            shape.enabled = false;
            Renderer sampledRenderer = particles.GetComponent<Renderer>();
            sampledRenderer.sharedMaterial = material;
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particles.Play();
            particles.Emit(1);
            particles.Pause();
            if (Environment.GetEnvironmentVariable("NATIVEH3VR_VERTEX_PARTICLE_DIRECT_GEOMETRY") == "1")
            {
                stage.SetActive(false);
                var plane = new GameObject("Particle vertex input control") { layer = SampleLayer };
                plane.transform.SetParent(root.transform, false);
                plane.transform.localPosition = Vector3.forward * 5;
                const float halfSize = 0.5f;
                var mesh = new Mesh
                {
                    vertices = new[] {new Vector3(-halfSize, -halfSize), new Vector3(-halfSize, halfSize),
                        new Vector3(halfSize, halfSize), new Vector3(halfSize, -halfSize)},
                    uv = new[] {Vector2.zero, Vector2.up, Vector2.one, Vector2.right},
                    triangles = new[] {0, 1, 2, 0, 2, 3},
                    normals = new[] {Vector3.back, Vector3.back, Vector3.back, Vector3.back},
                };
                var sampleColor = new Color32(255, 255, 255, 128);
                mesh.colors32 = new[] {sampleColor, sampleColor, sampleColor, sampleColor};
                plane.AddComponent<MeshFilter>().sharedMesh = mesh;
                sampledRenderer = plane.AddComponent<MeshRenderer>();
                sampledRenderer.sharedMaterial = material;
            }
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;

            yield return null;
            yield return new WaitForEndOfFrame();
            Save(target, Path.Combine(output, "installed.png"));
            File.AppendAllText(Path.Combine(output, "state.txt"), material.shader.name + "|" + material.renderQueue + "|" +
                material.GetTag("RenderType", false) + "|" + original.gameObject.name + "\n");
#if UNITY_6000_0_OR_NEWER
            string bundlePath = Environment.GetEnvironmentVariable("NATIVEH3VR_VERTEX_PARTICLE_BUNDLE");
            if (!string.IsNullOrEmpty(bundlePath))
            {
                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (!bundle) throw new InvalidOperationException("The local particle shader bundle failed to load");
                Shader candidate = null;
                foreach (var shader in bundle.LoadAllAssets<Shader>())
                    if (shader.name == "NativeH3VR/Stock Vertex-Lit Particles")
                        candidate = shader;
                if (!candidate || !candidate.isSupported) throw new InvalidOperationException("The candidate shader is unavailable");
                var replacement = new Material(material);
                replacement.shader = candidate;
                replacement.renderQueue = -1;
                replacement.SetOverrideTag("RenderType", "Transparent");
                if (!replacement.GetTexture("_MainTex"))
                    replacement.SetTexture("_MainTex", material.GetTexture("_BaseMap"));
                sampledRenderer.sharedMaterial = replacement;
                yield return null;
                yield return new WaitForEndOfFrame();
                Save(target, Path.Combine(output, "replacement.png"));
                File.AppendAllText(Path.Combine(output, "state.txt"), replacement.shader.name + "|" + replacement.renderQueue + "|" +
                    replacement.GetTag("RenderType", false) + "\n");
            }
#endif
            File.WriteAllText(Path.Combine(output, "complete.txt"), "complete\n");
            Application.Quit();
        }

        private static void Save(RenderTexture source, string path)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = source;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            RenderTexture.active = previous;
            Destroy(texture);
        }
    }
}
