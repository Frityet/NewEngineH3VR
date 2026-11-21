using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering;
#endif

namespace NativeH3VR.QA
{
    // This test also compiles against the retail Unity 5.6 player.
    public sealed class OpticBlitWitness : MonoBehaviour
    {
        private const int Size = 96;
        private const string OutputVariable = "NATIVEH3VR_OPTIC_BLIT_WITNESS";
        private static readonly string[] PassNames =
        {
            "EXTERNALNVG", "NVGLUMINOSITYBLEND", "THERMALPOSTPROCESS", "THERMALTEMPORALBLEND",
            "IRFILTERBLUR", "EXTERNALNVGCROP", "NVGSTREAKING", "NVGBLUR"
        };

        public static void Initialize()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OutputVariable))) return;
#if UNITY_6000_0_OR_NEWER
            if (FindAnyObjectByType<OpticBlitWitness>()) return;
#else
            if (FindObjectOfType<OpticBlitWitness>()) return;
#endif
            var host = new GameObject("Stock optic pass witness");
            DontDestroyOnLoad(host);
            host.AddComponent<OpticBlitWitness>();
        }

        private IEnumerator Start()
        {
            string output = Environment.GetEnvironmentVariable(OutputVariable);
            Directory.CreateDirectory(output);
#if UNITY_6000_0_OR_NEWER && !NATIVEH3VR_GAME_WITNESS
            yield return null;
#else
            while (SceneManager.GetActiveScene().name != "MainMenu3") yield return null;
            yield return new WaitForSecondsRealtime(5);
#if !UNITY_6000_0_OR_NEWER
            // The fixture uses a monoscopic source texture in every player.
            UnityEngine.VR.VRSettings.enabled = false;
#endif
#endif
            Time.captureFramerate = 90;
            yield return null;
            yield return null;
            try
            {
                Capture(output);
                Application.Quit();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "failed.txt"), error.ToString());
                Debug.LogException(error);
                Application.Quit();
            }
        }

        private static Texture2D Input(bool overlay)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            var colors = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float u = x / (Size - 1f);
                float v = y / (Size - 1f);
                bool stripe = ((x / 12) & 1) == 0;
                colors[x + y * Size] = overlay
                    ? new Color(.1f, .2f + .6f * v, stripe ? .7f : .25f, u)
                    : new Color(.1f + .8f * u, .1f + .6f * v, stripe ? .4f : .2f, .8f);
            }
            texture.SetPixels(colors);
            texture.Apply(true);
            return texture;
        }

        private static Material Prepare(Texture2D input, Texture2D overlay)
        {
#if UNITY_6000_0_OR_NEWER && !NATIVEH3VR_GAME_WITNESS
            var shader = Resources.Load<Shader>("Optics");
#else
            var actual = PIPScope.GetPostProcessMaterial();
            var shader = actual ? actual.shader : null;
#endif
            if (!shader || !shader.isSupported)
                throw new InvalidOperationException("Missing or unsupported stock optic postprocess.");
            var material = new Material(shader);
            if (material.passCount != PassNames.Length)
                throw new InvalidOperationException("Unexpected stock optic pass count " + material.passCount);
            material.SetTexture("_ThermalOverlay", overlay);
            material.SetTexture("_LuminosityTexture", input);
            material.SetTexture("_ScopeViewTexture", input);
            material.SetTexture("_ThermalLUT", input);
            material.SetVector("_ThermalOutDimensions", new Vector4(Size, Size, 0, 0));
            // Extend the NVG mask past its clipping radius at the corners so
            // the external overlay's out-of-lens contribution is visible.
            material.SetVector("_ScopeViewOffsetScale", new Vector4(0, 0, 2, 2));
            material.SetVector("_MainTexUVS", new Vector4(0, 0, 1, 1));
            material.SetFloat("_ThermalBrightFactor", 1);
            material.SetFloat("_ThermalDarkIntensity", 1);
            material.SetFloat("_ThermalDigitalZoom", 1);
            material.SetFloat("_ThermalNoise", 0);
            material.SetFloat("_ThermalEdgeDetectScale", 0);
            material.SetFloat("_VignetteFlat", 1);
            material.SetFloat("_LuminosityLod", 0);
            material.SetFloat("_LuminosityBlend", .7f);
            material.SetFloat("_NightVisionSNR", .05f);
            material.SetFloat("_NightVisionHalo", .5f);
            material.SetVector("_NightVisionLuminosityFunction", new Vector4(.2126f, .7152f, .0722f, 0));
            material.SetColor("_NightVisionFilter", Color.white);
            material.SetFloat("_BlurRadius", 2);
            return material;
        }

        private static void Capture(string output)
        {
            var input = Input(false);
            var overlay = Input(true);
            var material = Prepare(input, overlay);
            var target = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            target.Create();
            var readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            try
            {
                foreach (bool enabled in new[] { false, true })
                {
                    if (enabled) material.EnableKeyword("SCOPE_PP_USE_OVERLAY");
                    else material.DisableKeyword("SCOPE_PP_USE_OVERLAY");

                    for (int i = 0; i < PassNames.Length; i++)
                    {
                        if (material.GetPassName(i) != PassNames[i])
                            throw new InvalidOperationException("Unexpected stock optic pass at index " + i);
                        RenderTexture.active = target;
                        GL.Clear(true, true, new Color(.12f, .17f, .22f, 1));
                        Graphics.Blit(input, target, material, i);
                        RenderTexture.active = target;
                        readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                        readback.Apply();
                        string file = PassNames[i] + (enabled ? "-overlay" : "-plain") + ".png";
                        File.WriteAllBytes(Path.Combine(output, file), readback.EncodeToPNG());
                    }
                }
                string pipeline = "Built-in";
#if UNITY_6000_0_OR_NEWER
                if (GraphicsSettings.currentRenderPipeline) pipeline = GraphicsSettings.currentRenderPipeline.GetType().Name;
#endif
                File.WriteAllLines(Path.Combine(output, "passes.txt"),
                    new[] { Application.unityVersion, SystemInfo.graphicsDeviceType.ToString(), pipeline,
                        QualitySettings.activeColorSpace.ToString(), string.Join(", ", PassNames),
                        Time.deltaTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
                File.WriteAllLines(Path.Combine(output, "inputs.txt"), new[]
                {
                    "shader=" + material.shader.name,
                    "size=" + Size,
                    "nightVisionSNR=" + material.GetFloat("_NightVisionSNR").ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    "nightVisionHalo=" + material.GetFloat("_NightVisionHalo").ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            finally
            {
                RenderTexture.active = null;
                target.Release();
                Destroy(target);
                Destroy(readback);
                Destroy(material);
                Destroy(overlay);
                Destroy(input);
            }
        }
    }
}
