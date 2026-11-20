using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace EngineValidation
{
    public sealed class Probe : MonoBehaviour
    {
        private const int Width = 320;
        private const int Height = 240;
        private const int RasterLayer = 30;
        private static readonly Color Yellow = new Color(1, 1, 0, 1);

        public Shader rasterShader;

        private readonly List<Check> checks = new List<Check>();
        private readonly List<string> captures = new List<string>();
        private string output;
        private Mesh quad;

        [Serializable]
        private sealed class Check
        {
            public string name;
            public bool passed;
            public string detail;
        }

        [Serializable]
        private sealed class Report
        {
            public string version;
            public string graphicsDevice;
            public bool passed;
            public Check[] checks;
            public string[] captures;
        }

        private void Start()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-probe-output");
                if (index < 0 || index + 1 >= args.Length || !Path.IsPathRooted(args[index + 1]))
                    throw new ArgumentException("Provide -probe-output followed by an absolute directory.");

                output = args[index + 1];
                Directory.CreateDirectory(output);
                RunChecks();
            }
            catch (Exception error)
            {
                Record("probe-exception", false, error.ToString());
            }

            var report = new Report
            {
                version = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                passed = checks.Count != 0 && checks.TrueForAll(check => check.passed),
                checks = checks.ToArray(),
                captures = captures.ToArray()
            };
            try
            {
                if (output != null)
                    File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Application.Quit(1);
                return;
            }

            Application.Quit(report.passed ? 0 : 1);
        }

        private void RunChecks()
        {
#if UNITY_STANDALONE_WIN
            const GraphicsDeviceType expectedDevice = GraphicsDeviceType.Direct3D11;
#elif UNITY_STANDALONE_LINUX
            const GraphicsDeviceType expectedDevice = GraphicsDeviceType.Vulkan;
#else
            const GraphicsDeviceType expectedDevice = GraphicsDeviceType.Metal;
#endif
            Record("graphics-device", SystemInfo.graphicsDeviceType == expectedDevice,
                $"Expected {expectedDevice}, observed {SystemInfo.graphicsDeviceType}");
            Record("raster-shader", rasterShader != null && rasterShader.isSupported,
                "Original color and texture shader is supported by the player graphics device");
            CheckTransforms();
            CheckPhysics();

            quad = new Mesh { name = "Front-facing probe quad" };
            quad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0),
                new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0)
            };
            quad.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();

            Camera camera = new GameObject("Probe camera").AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 1 << RasterLayer;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.aspect = (float)Width / Height;

            CheckRaster(camera);
            CheckStereo(camera);
        }

        private void CheckRaster(Camera camera)
        {
            camera.orthographic = true;
            camera.orthographicSize = 3;
            camera.nearClipPlane = 1;
            camera.farClipPlane = 30;
            camera.transform.position = new Vector3(0, 0, -10);

            Transform stage = new GameObject("Raster tests").transform;
            // Draw green first so the later red draw must pass the GPU depth test.
            AddQuad(stage, -3, 2, 1, 1.6f, MakeMaterial(Color.red, 2001));
            AddQuad(stage, -3, 2, 0, 0.8f, MakeMaterial(Color.green));
            AddQuad(stage, -1, 2, 1, 1.4f, MakeMaterial(Color.blue));

            Material transparent = MakeMaterial(new Color(1, 0, 0, 0.5f), 3000);
            transparent.SetInt("_ZWrite", 0);
            transparent.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            transparent.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            AddQuad(stage, -1, 2, 0, 0.9f, transparent);

            Material culled = MakeMaterial(Yellow);
            culled.SetInt("_Cull", (int)CullMode.Back);
            AddQuad(stage, 1, 2, 0, 1.25f, culled);
            AddQuad(stage, 3, 2, 0, 1.25f, culled).transform.rotation = Quaternion.Euler(0, 180, 0);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(new Color32[] { Color.red, Color.green, Color.blue, Yellow });
            texture.Apply();
            Material textured = MakeMaterial(Color.white);
            textured.mainTexture = texture;
            AddQuad(stage, -3, 0, 0, 1.6f, textured);

            AddQuad(stage, -1, 0, -9.5f, 1.25f, MakeMaterial(Color.white));
            AddQuad(stage, 1, 0, 21, 1.25f, MakeMaterial(Color.white));
            AddQuad(stage, 4, 0, 0, 1.3f, MakeMaterial(Color.cyan));
            AddQuad(stage, -3, -2, 0, 1.25f, MakeMaterial(Color.white)).layer = 0;

            Color32[] image = Capture(camera, "raster");
            CheckPixel(camera, image, "depth-rejects-later-far-draw", -3, 2, Color.green);
            CheckPixel(camera, image, "far-draw-remains-visible-around-occluder", -3.6f, 2, Color.red);
            CheckPixel(camera, image, "alpha-blend", -1, 2, new Color32(128, 0, 128, 255));
            CheckPixel(camera, image, "opaque-background-under-blend", -1.6f, 2, Color.blue);
            CheckPixel(camera, image, "front-face-visible", 1, 2, Yellow);
            CheckPixel(camera, image, "back-face-culled", 3, 2, Color.black);
            CheckPixel(camera, image, "point-texture-bottom-left", -3.4f, -0.4f, Color.red);
            CheckPixel(camera, image, "point-texture-bottom-right", -2.6f, -0.4f, Color.green);
            CheckPixel(camera, image, "point-texture-top-left", -3.4f, 0.4f, Color.blue);
            CheckPixel(camera, image, "point-texture-top-right", -2.6f, 0.4f, Yellow);
            CheckPixel(camera, image, "near-plane-clip", -1, 0, Color.black);
            CheckPixel(camera, image, "far-plane-clip", 1, 0, Color.black);
            CheckPixel(camera, image, "geometry-visible-at-viewport-edge", 3.85f, 0, Color.cyan);
            CheckPixel(camera, image, "camera-layer-exclusion", -3, -2, Color.black);
            stage.gameObject.SetActive(false);
        }

        private void CheckStereo(Camera camera)
        {
            camera.orthographic = false;
            camera.fieldOfView = 50;
            camera.nearClipPlane = 0.1f;
            const float halfEyeSeparation = 0.032f;
            Transform stage = new GameObject("Stereo markers").transform;
            AddQuad(stage, 0, 0.35f, 3, 0.45f, MakeMaterial(Color.red));
            AddQuad(stage, 0.6f, -0.65f, 7, 0.65f, MakeMaterial(Color.green));

            camera.transform.position = new Vector3(-halfEyeSeparation, 0, 0);
            Color32[] left = Capture(camera, "stereo-left");
            camera.transform.position = new Vector3(halfEyeSeparation, 0, 0);
            Color32[] right = Capture(camera, "stereo-right");

            Vector2 redLeft = MarkerCenter(left, true);
            Vector2 redRight = MarkerCenter(right, true);
            Vector2 greenLeft = MarkerCenter(left, false);
            Vector2 greenRight = MarkerCenter(right, false);
            float nearParallax = redLeft.x - redRight.x;
            float farParallax = greenLeft.x - greenRight.x;
            Record("stereo-horizontal-parallax", farParallax >= 1 && nearParallax > farParallax + 1,
                FormattableString.Invariant($"Near parallax {nearParallax:F3} px, far parallax {farParallax:F3} px"));
            bool aligned = Mathf.Abs(redLeft.y - redRight.y) <= 1 && Mathf.Abs(greenLeft.y - greenRight.y) <= 1;
            bool upright = redLeft.y > greenLeft.y + 15 && redRight.y > greenRight.y + 15;
            Record("stereo-vertical-alignment", aligned && upright,
                FormattableString.Invariant($"Left marker heights {redLeft.y:F3}/{greenLeft.y:F3} px, right {redRight.y:F3}/{greenRight.y:F3} px"));
        }

        private void CheckTransforms()
        {
            Transform parent = new GameObject("Parent transform").transform;
            parent.SetPositionAndRotation(new Vector3(3, -2, 1), Quaternion.Euler(20, 30, 40));
            parent.localScale = new Vector3(2, 3, 4);
            Transform child = new GameObject("Child transform").transform;
            child.SetParent(parent, false);
            child.localPosition = new Vector3(1, 2, -3);
            child.localRotation = Quaternion.Euler(10, -20, -30);
            Vector3 point = new Vector3(0.2f, -0.4f, 0.6f);
            Vector3 position = child.TransformPoint(point);
            float composedError = Vector3.Distance(position, parent.localToWorldMatrix.MultiplyPoint3x4(
                child.localPosition + child.localRotation * point));
            float inverseError = Vector3.Distance(point, child.InverseTransformPoint(position));
            Record("parented-transform-round-trip", composedError < 0.0001f && inverseError < 0.0001f,
                FormattableString.Invariant($"Composition error {composedError:F6}, inverse error {inverseError:F6}"));
        }

        private void CheckPhysics()
        {
            Scene scene = SceneManager.CreateScene("Probe physics", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            PhysicsScene physics = scene.GetPhysicsScene();
            var floor = new GameObject("Probe floor");
            var sphere = new GameObject("Probe sphere");
            SceneManager.MoveGameObjectToScene(floor, scene);
            SceneManager.MoveGameObjectToScene(sphere, scene);
            floor.transform.position = new Vector3(0, -0.25f, 0);
            floor.AddComponent<BoxCollider>().size = new Vector3(6, 0.5f, 6);
            sphere.transform.position = new Vector3(0, 3, 0);
            sphere.AddComponent<SphereCollider>().radius = 0.5f;
            Rigidbody body = sphere.AddComponent<Rigidbody>();
            Physics.SyncTransforms();

            for (int step = 0; step < 10; step++)
                physics.Simulate(0.02f);
            float fallingHeight = body.position.y;
            Record("fixed-step-gravity", fallingHeight > 2.7f && fallingHeight < 2.9f,
                FormattableString.Invariant($"Height after 0.2 s {fallingHeight:F4}"));

            for (int step = 0; step < 140; step++)
                physics.Simulate(0.02f);
            float restingHeight = body.position.y;
            Record("physics-contact", Mathf.Abs(restingHeight - 0.5f) < 0.025f,
                FormattableString.Invariant($"Sphere resting height {restingHeight:F4}"));

            bool hit = physics.Raycast(new Vector3(0, 4, 0), Vector3.down, out RaycastHit contact, 6);
            Record("physics-raycast", hit && contact.collider.gameObject == sphere && Mathf.Abs(contact.point.y - 1) < 0.03f,
                FormattableString.Invariant($"Ray hit {hit}, height {contact.point.y:F4}"));
        }

        private Material MakeMaterial(Color color, int queue = 2000)
        {
            return new Material(rasterShader) { color = color, renderQueue = queue };
        }

        private GameObject AddQuad(Transform stage, float x, float y, float z, float size, Material material)
        {
            var item = new GameObject("Probe quad") { layer = RasterLayer };
            item.transform.SetParent(stage, false);
            item.transform.localPosition = new Vector3(x, y, z);
            item.transform.localScale = new Vector3(size, size, 1);
            item.AddComponent<MeshFilter>().sharedMesh = quad;
            item.AddComponent<MeshRenderer>().sharedMaterial = material;
            return item;
        }

        private Color32[] Capture(Camera camera, string name)
        {
            RenderTexture target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            var reader = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                reader.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                Color32[] pixels = reader.GetPixels32();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), reader.EncodeToPNG());
                WritePpm(Path.Combine(output, name + ".ppm"), pixels);
                captures.Add(name + ".png");
                return pixels;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Destroy(reader);
            }
        }

        private static void WritePpm(string path, Color32[] pixels)
        {
            byte[] header = Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n");
            using (FileStream file = File.Create(path))
            {
                file.Write(header, 0, header.Length);
                var row = new byte[Width * 3];
                for (int y = Height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        Color32 pixel = pixels[y * Width + x];
                        row[x * 3] = pixel.r;
                        row[x * 3 + 1] = pixel.g;
                        row[x * 3 + 2] = pixel.b;
                    }
                    file.Write(row, 0, row.Length);
                }
            }
        }

        private void CheckPixel(Camera camera, Color32[] pixels, string name, float x, float y, Color32 expected)
        {
            Vector3 uv = camera.WorldToViewportPoint(new Vector3(x, y, 0));
            var actual = pixels[(int)(uv.y * Height) * Width + (int)(uv.x * Width)];
            bool matches = Mathf.Abs(expected.r - actual.r) <= 3 && Mathf.Abs(expected.g - actual.g) <= 3 &&
                Mathf.Abs(expected.b - actual.b) <= 3;
            Record(name, matches, $"Expected RGB {expected.r}/{expected.g}/{expected.b}, observed {actual.r}/{actual.g}/{actual.b}");
        }

        private static Vector2 MarkerCenter(Color32[] pixels, bool red)
        {
            int count = 0;
            var sum = Vector2.zero;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                Color32 pixel = pixels[y * Width + x];
                if (pixel.b > 5 || (red ? pixel.r < 250 || pixel.g > 5 : pixel.g < 250 || pixel.r > 5))
                    continue;
                count++;
                sum += new Vector2(x, y);
            }
            return count >= 32 ? sum / count : new Vector2(float.NaN, float.NaN);
        }

        private void Record(string name, bool passed, string detail)
        {
            checks.Add(new Check { name = name, passed = passed, detail = detail });
            string message = $"{name}: {detail}";
            if (passed)
                Debug.Log(message);
            else
                Debug.LogError(message);
        }
    }
}
