using System;
using System.Collections;
using System.IO;
using System.Linq;
using FistVR;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NativeH3VR.QA
{
    public sealed class GraphicsOptionsWitness : MonoBehaviour
    {
        private string output;
        private bool verifySaved;
        private bool failed;

        public static void Initialize()
        {
            var host = new GameObject("Graphics Options witness");
            DontDestroyOnLoad(host);
            host.AddComponent<GraphicsOptionsWitness>();
        }

        private IEnumerator Start()
        {
            output = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_UI_WITNESS");
            if (string.IsNullOrEmpty(output))
                yield break;
            verifySaved = Environment.GetEnvironmentVariable("NATIVEH3VR_GRAPHICS_UI_RESTART") == "1";

            float deadline = Time.realtimeSinceStartup + 110;
            FVRWristMenu menu;
            FVRViveHand hand;
            do
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    Finish(new TimeoutException("Waiting for stock MainMenu3 wrist menu and controller"));
                    yield break;
                }
                yield return null;
                menu = FindObjectsByType<FVRWristMenu>(FindObjectsInactive.Include).FirstOrDefault(candidate => candidate.OptionsPanelPrefab);
                hand = FindObjectsByType<FVRViveHand>().FirstOrDefault();
            } while (SceneManager.GetActiveScene().name != "MainMenu3" || !menu || !hand || GM.CurrentSceneSettings == null);

            // Use the actual stock prefab. Construct it after the scene has already loaded to exercise wrist-spawn timing.
            var root = Instantiate(menu.OptionsPanelPrefab, new Vector3(1024f, 1024f, 1024f), Quaternion.identity);
            var manager = root.GetComponentInChildren<OptionsPanel_Screenmanager>(true);
            if (!manager || !manager.OPS_Quality)
            {
                Finish(new InvalidOperationException("Stock options prefab lacks the graphics screen"));
                yield break;
            }

            if (!verifySaved)
            {
                yield return CheckViewFade("default-");
                if (failed) yield break;
            }

            try
            {
                manager.SetScreen(0);
                var graphics = manager.transform.GetComponentsInChildren<FVRPointableButton>()
                    .Single(control => control.name == "Button_Option_3_Graphics");
                Press(graphics.transform, hand);

                var nativePage = manager.OPS_Quality.transform.Find("Canvas/Native Graphics Settings");
                Require(nativePage, "Native graphics did not attach when the late-created panel initialized");
                CheckPanel(manager, hand, graphics, nativePage);
            }
            catch (Exception error)
            {
                Finish(error);
                yield break;
            }
            if (!verifySaved)
            {
                yield return CheckViewFade("");
                if (failed) yield break;
            }
            (int reflections, int fog, int receivers) effectsBefore;
            int[] historyBefore;
            yield return new WaitForSecondsRealtime(1f);
            try
            {
                effectsBefore = GraphicsPresetControls.ReadEffects();
                historyBefore = GraphicsPresetControls.ReadTemporalHistories();
            }
            catch (Exception error) { Finish(error); yield break; }
            yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForEndOfFrame();
            try
            {
                GraphicsPresetControls.CheckUrp(3, NativeStereoRenderer.Instance,
                    Path.Combine(output, (verifySaved ? "restart" : "interaction") + "-custom-asset.txt"),
                    "1440 1600", effectsBefore, historyBefore, expectedRenderScale: 1.15f, expectedSamples: 1);
                GraphicsPresetControls.CheckUnrelatedRenderRequest(
                    Path.Combine(output, (verifySaved ? "restart" : "interaction") + "-still-history.txt"));
            }
            catch (Exception error)
            {
                Finish(error);
                yield break;
            }
            Finish(null);
        }

        private const int FadeCropSize = 32;
        private const int MinimumStableFadePixels = 128;

        private IEnumerator SettleFade()
        {
            var stereo = NativeStereoRenderer.Instance;
            if (!stereo)
            {
                Finish(new InvalidOperationException("The native stereo renderer is unavailable for the fade witness"));
                yield break;
            }
            ulong target = stereo.CompletedFrames + 16;
            float deadline = Time.realtimeSinceStartup + 15f;
            while (stereo.CompletedFrames < target)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    Finish(new TimeoutException("Waiting for 16 settled stereo pairs after the stock fade changed"));
                    yield break;
                }
                yield return null;
            }
            yield return new WaitForEndOfFrame();
        }

        private IEnumerator CheckViewFade(string profile)
        {
            Valve.VR.SteamVR_Fade.Start(Color.clear, 0f);
            yield return SettleFade();
            if (failed) yield break;
            Color32[][] before;
            try { before = CaptureViewFade(profile + "uncovered"); }
            catch (Exception error) { Finish(error); yield break; }

            Valve.VR.SteamVR_Fade.Start(new Color(1f, 0f, 0f, 0.5f), 0f);
            yield return SettleFade();
            if (failed) yield break;
            Color32[][] blended;
            try { blended = CaptureViewFade(profile + "half"); }
            catch (Exception error) { Finish(error); yield break; }

            Valve.VR.SteamVR_Fade.Start(Color.clear, 0f);
            yield return SettleFade();
            if (failed) yield break;
            try
            {
                var after = CaptureViewFade(profile + "reference-after");
                for (int eye = 0; eye < 2; eye++)
                {
                    var metrics = MeasureFade(before[eye], after[eye], blended[eye]);
                    File.AppendAllText(Path.Combine(output, "view-fade-metrics.txt"), FormattableString.Invariant(
                        $"{profile}eye{eye}: stablePixels={metrics.count}, stableRgbError={metrics.error:F3}, fullRgbError={metrics.fullError:F3}\n"));
                    Require(metrics.count >= MinimumStableFadePixels,
                        $"The uncovered reference did not supply enough stable pixels in eye {eye}: {metrics.count}");
                    Require(metrics.error <= 3, $"Half-alpha stock fade has {metrics.error:F2} stable RGB error in eye {eye}");
                }
            }
            catch (Exception error) { Finish(error); yield break; }

            Valve.VR.SteamVR_Fade.Start(Color.red, 0f);
            yield return SettleFade();
            if (failed) yield break;
            try
            {
                var opaque = CaptureViewFade(profile + "opaque");
                for (int eye = 0; eye < 2; eye++)
                    Require(opaque[eye].All(p => p.r >= 254 && p.g <= 1 && p.b <= 1),
                        $"Opaque stock fade changed its authored color in eye {eye}");
            }
            catch (Exception error) { Finish(error); yield break; }
            finally { Valve.VR.SteamVR_Fade.Start(Color.clear, 0.2f); }

            // The stock transition advances in game time, including during a slow synthetic replay.
            yield return new WaitForSeconds(0.4f);
            try
            {
                var cleared = CaptureViewFade(profile + "cleared");
                for (int eye = 0; eye < 2; eye++)
                    Require(cleared[eye].Count(p => p.r > p.g + 35 && p.r > p.b + 35) <= cleared[eye].Length * 0.05f,
                        $"Stock fade failed to clear in eye {eye}");
            }
            catch (Exception error) { Finish(error); }
        }

        private static (int count, double error, double fullError) MeasureFade(
            Color32[] before, Color32[] after, Color32[] blended)
        {
            int count = 0;
            double sum = 0, fullSum = 0;
            for (int n = 0; n < before.Length; n++)
            {
                double error = BlendError(before[n], after[n], blended[n]);
                fullSum += error;
                int x = n % FadeCropSize, y = n / FadeCropSize;
                if (x < 2 || y < 2 || x >= FadeCropSize - 2 || y >= FadeCropSize - 2 ||
                    !Matches(before[n], after[n], 4) || !FlatNeighborhood(before, n) || !FlatNeighborhood(after, n))
                    continue;
                sum += error;
                count++;
            }
            return (count, count > 0 ? sum / count : double.PositiveInfinity, fullSum / before.Length);
        }

        // Choose the reference pixels without consulting the faded capture. Flat interiors
        // stay comparable across the two TAA jitter sequences and minor tracking changes.
        private static bool FlatNeighborhood(Color32[] pixels, int center)
        {
            int rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                var c = pixels[center + y * FadeCropSize + x];
                rMin = Math.Min(rMin, c.r); rMax = Math.Max(rMax, c.r);
                gMin = Math.Min(gMin, c.g); gMax = Math.Max(gMax, c.g);
                bMin = Math.Min(bMin, c.b); bMax = Math.Max(bMax, c.b);
            }
            return rMax - rMin <= 8 && gMax - gMin <= 8 && bMax - bMin <= 8;
        }

        private static bool Matches(Color32 a, Color32 b, int tolerance) =>
            Math.Abs(a.r - b.r) <= tolerance && Math.Abs(a.g - b.g) <= tolerance && Math.Abs(a.b - b.b) <= tolerance;

        private static double BlendError(Color32 before, Color32 after, Color32 blended)
        {
            static float Encoded(float a, float b, float fade)
            {
                float linear = (Mathf.GammaToLinearSpace(a / 255f) + Mathf.GammaToLinearSpace(b / 255f)) * 0.5f;
                return 255 * Mathf.LinearToGammaSpace((linear + fade) * 0.5f);
            }
            return (Mathf.Abs(Encoded(before.r, after.r, 1f) - blended.r) +
                    Mathf.Abs(Encoded(before.g, after.g, 0f) - blended.g) +
                    Mathf.Abs(Encoded(before.b, after.b, 0f) - blended.b)) / 3.0;
        }

        private Color32[][] CaptureViewFade(string state)
        {
            var stereo = NativeStereoRenderer.Instance;
            Require(stereo && stereo.SourceCamera && stereo.SourceCamera.GetComponent<Valve.VR.SteamVR_Fade>(),
                "The stock fade component is missing from the tracked camera");
            const int size = FadeCropSize;
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            var samples = new Color32[2][];
            try
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    var target = stereo.EyeTexture(eye);
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect((target.width - size) / 2, (target.height - size) / 2, size, size), 0, 0);
                    image.Apply(false);
                    samples[eye] = image.GetPixels32();
                    File.WriteAllBytes(Path.Combine(output, $"view-fade-{state}-{eye}.png"), image.EncodeToPNG());
                }
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(image);
            }
            return samples;
        }

        private void CheckPanel(OptionsPanel_Screenmanager manager, FVRViveHand hand,
            FVRPointableButton graphics, Transform nativePage)
        {
            var quality = manager.OPS_Quality.transform;
            Require(nativePage.gameObject.activeInHierarchy, "Stock Graphics Options button did not open the native page");

            var canvas = nativePage.GetComponentInParent<Canvas>(true);
            var effects = quality.Find("Canvas/Graphics Effects Settings");
            var game = quality.Find("Canvas/Stock Game Graphics Settings");
            var navigation = quality.Find("Canvas/Graphics Page Navigation");
            var effectsTab = navigation.Find("EffectsPage");
            var gameTab = navigation.Find("GraphicsPage");
            var back = quality.Find("Canvas/Button_Back (3)");
            var renderScale = nativePage.Find("RenderScale Value").GetComponent<Text>();
            var preset = nativePage.Find("Preset Value").GetComponent<Text>();
            var specular = nativePage.Find("SpecularFiltering Value").GetComponent<Text>();
            var characterShadows = nativePage.Find("CharacterShadows Value").GetComponent<Text>();
            var radius = effects.Find("BloomRadius Value").GetComponent<Text>();
            var exposure = effects.Find("Exposure Value").GetComponent<Text>();
            var antialiasing = nativePage.Find("Msaa Value").GetComponent<Text>();
            var fog = effects.Find("FogQuality Value").GetComponent<Text>();
            var reflections = effects.Find("Reflections Value").GetComponent<Text>();
            var tonemapping = effects.Find("Tonemapping Value").GetComponent<Text>();
            var expectedExposure = Path.Combine(output, "exposure.txt");
            var expectedPath = Path.Combine(output, "render-scale.txt");
            var stateDirectory = Environment.GetEnvironmentVariable("NATIVEH3VR_STATE_DIR");
            Require(!string.IsNullOrEmpty(stateDirectory), "Graphics Options witness requires isolated launcher state");
            string settingsPath = Path.Combine(stateDirectory, "graphics-options.json");
            string savedSettings = Path.Combine(output, "saved-custom.json");
            if (verifySaved)
            {
                Require(File.Exists(settingsPath) && File.ReadAllText(settingsPath) == File.ReadAllText(savedSettings) &&
                        preset.text == "Custom" && renderScale.text == File.ReadAllText(expectedPath),
                    "Custom settings or render scale were lost across restart");
                Require(specular.text == "Low" && characterShadows.text == "Enhanced",
                    "Specular filtering or character shadows were lost across restart");
                Press(effectsTab, hand);
                Require(effects.gameObject.activeInHierarchy && exposure.text == File.ReadAllText(expectedExposure) &&
                        tonemapping.text == "ACES" && radius.text == "Tight" && antialiasing.text == "TAA" &&
                        fog.text == "Enhanced" && reflections.text == "Screen-space", "Effect choices were lost across restart");
                return;
            }

            Require(!File.Exists(settingsPath) && preset.text == "Balanced" && renderScale.text == "100%" &&
                    antialiasing.text == "2× MSAA" && fog.text == "Off" && reflections.text == "Off" &&
                    specular.text == "Low" && characterShadows.text == "Scene" && exposure.text == "0 EV" && tonemapping.text == "Game" && radius.text == "Standard",
                "Balanced did not open with its scene grade and default filters");
            Press(nativePage.Find("Preset Next"), hand);
            Require(preset.text == "Quality" && specular.text == "High" && radius.text == "Wide" &&
                    antialiasing.text == "4× MSAA" && characterShadows.text == "Scene" && fog.text == "Scene" && reflections.text == "Off" &&
                    exposure.text == "0 EV" && tonemapping.text == "Game", "Unexpected Quality graphics choices");
            Press(nativePage.Find("Preset Next"), hand);
            Require(preset.text == "Ultra" && antialiasing.text == "TAA" && renderScale.text == "100%" &&
                    characterShadows.text == "Enhanced" && fog.text == "Enhanced" && reflections.text == "Screen-space",
                "Unexpected Ultra graphics choices");
            Press(nativePage.Find("Msaa Next"), hand);
            Require(preset.text == "Custom" && antialiasing.text == "Off", "TAA did not wrap to disabled anti-aliasing");
            Press(nativePage.Find("Msaa Previous"), hand);
            Require(preset.text == "Ultra" && antialiasing.text == "TAA", "Restoring TAA did not restore Ultra");
            Press(nativePage.Find("Preset Next"), hand);
            Require(preset.text == "Balanced", "Preset arrow did not wrap from Ultra to Balanced");
            Press(nativePage.Find("Preset Next"), hand);
            Require(preset.text == "Quality", "Preset arrow did not return to Quality");
            Press(nativePage.Find("RenderScale Next"), hand);
            Require(renderScale.text == "115%" && preset.text == "Custom", "The render-scale arrow did not select Custom at 115%");
            RoundTrip(nativePage, hand, "RenderScale", "Msaa", "ShadowQuality", "ShadowDistance", "AdditionalLights",
                "AmbientOcclusion", "Hdr", "SpecularFiltering", "CharacterShadows");
            Press(nativePage.Find("CharacterShadows Next"), hand);
            Require(characterShadows.text == "Enhanced", "The character shadows arrow did not select Enhanced");
            Press(nativePage.Find("Msaa Next"), hand);
            Require(antialiasing.text == "TAA", "The anti-aliasing control did not select TAA");
            Press(nativePage.Find("SpecularFiltering Previous"), hand);
            Require(specular.text == "Low", "The specular filtering control did not change to Low");
            CapturePage(canvas, back.forward, "native-graphics.png");

            Press(effectsTab, hand);
            Require(effects.gameObject.activeInHierarchy && !nativePage.gameObject.activeInHierarchy, "Effects did not open");
            RoundTrip(effects, hand, "Bloom", "BloomRadius", "Sharpening", "Tonemapping", "Exposure", "FogQuality", "Reflections");
            Press(effects.Find("FogQuality Next"), hand);
            Press(effects.Find("Reflections Next"), hand);
            Require(fog.text == "Enhanced" && reflections.text == "Screen-space",
                "The Effects arrows did not enable enhanced fog and screen-space reflections");
            Press(effects.Find("BloomRadius Next"), hand);
            Press(effects.Find("Exposure Next"), hand);
            Press(effects.Find("Tonemapping Previous"), hand);
            Require(radius.text == "Tight" && exposure.text == "+0.25 EV" && tonemapping.text == "ACES",
                "The effects arrows did not select the expected radius, exposure and tone curve");
            CapturePage(canvas, back.forward, "effects-graphics.png");

            Press(gameTab, hand);
            Require(game.gameObject.activeInHierarchy && !effects.gameObject.activeInHierarchy &&
                    manager.OPS_Quality.OBS_Post_CC.transform.IsChildOf(game), "Game Settings did not show the stock grading control");
            CapturePage(canvas, back.forward, "game-graphics.png");
            Press(gameTab, hand);
            Require(nativePage.gameObject.activeInHierarchy, "The Graphics tab did not return from Game Settings");
            Press(effectsTab, hand);
            Press(effectsTab, hand);
            Require(nativePage.gameObject.activeInHierarchy, "The Graphics tab did not return from Effects");
            Press(gameTab, hand);
            Press(back, hand);
            Require(!quality.gameObject.activeInHierarchy, "Stock Back did not return to options navigation");
            Press(graphics.transform, hand);
            Require(nativePage.gameObject.activeInHierarchy && renderScale.text == "115%" && preset.text == "Custom" &&
                    exposure.text == "+0.25 EV" && characterShadows.text == "Enhanced", "Reopening Graphics Options lost the selected settings");
            Require(File.Exists(settingsPath), "Graphics controls did not write to isolated launcher state");
            File.Copy(settingsPath, savedSettings);
            File.WriteAllText(expectedPath, renderScale.text);
            File.WriteAllText(expectedExposure, exposure.text);
        }

        private static void RoundTrip(Transform page, FVRViveHand hand, params string[] settings)
        {
            foreach (var setting in settings)
            {
                var value = page.Find(setting + " Value").GetComponent<Text>();
                string original = value.text;
                Press(page.Find(setting + " Next"), hand);
                Require(value.text != original, $"The {setting} next arrow did not change its displayed value");
                Press(page.Find(setting + " Previous"), hand);
                Require(value.text == original, $"The {setting} previous arrow did not restore {original}");
            }
        }

        private static void Press(Transform control, FVRViveHand hand)
        {
            if (!control || !control.TryGetComponent(out FVRPointableButton pointable) ||
                !control.TryGetComponent(out BoxCollider collider))
                throw new InvalidOperationException($"Missing pointable or collider on {control?.name}");
            Physics.SyncTransforms();
            var forward = control.forward;
            var origin = collider.bounds.center - forward * 0.025f;
            Require(Physics.Raycast(origin, forward, out var hit, Mathf.Min(0.09f, pointable.MaxPointingRange),
                        hand.PointingLayerMask, QueryTriggerInteraction.Collide) && hit.collider == collider,
                $"A stock controller ray did not hit {control.name}");

            var input = hand.Input;
            var previous = hand.CurrentPointable;
            try
            {
                hand.Input.TriggerDown = true;
                hand.CurrentPointable = hit.collider.GetComponent<FVRPointable>();
            }
            finally
            {
                hand.Input = input;
                hand.CurrentPointable = previous;
            }
        }

        private void CapturePage(Canvas canvas, Vector3 pointingDirection, string filename)
        {
            Require(canvas && canvas.renderMode == RenderMode.WorldSpace, "The stock graphics page must use an in-world canvas");
            Canvas.ForceUpdateCanvases();
            var corners = new Vector3[4];
            ((RectTransform)canvas.transform).GetWorldCorners(corners);
            float width = Vector3.Distance(corners[0], corners[3]);
            float height = Vector3.Distance(corners[0], corners[1]);
            Require(width > 0f && height > 0f, "The stock canvas has no visible extent");

            // The stock Back control establishes the panel's interaction side.
            // Use an orthographic view of that same face to keep the actual in-world
            // layout, with the remote panel isolated from the menu environment.
            var center = (corners[0] + corners[2]) * 0.5f;
            var normal = canvas.transform.forward;
            if (Vector3.Dot(normal, pointingDirection) < 0f) normal = -normal;
            float distance = Mathf.Max(width, height);
            float aspect = width / height;
            int imageWidth = Mathf.Clamp(Mathf.RoundToInt(1200 * aspect), 900, 2400);
            const int imageHeight = 1200;
            var target = new RenderTexture(imageWidth, imageHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var image = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
            var camera = new GameObject("Graphics panel capture camera").AddComponent<Camera>();
            var previousTarget = RenderTexture.active;
            try
            {
                camera.enabled = false;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(height, width * imageHeight / imageWidth) * 0.53f;
                camera.nearClipPlane = distance * 0.02f;
                camera.farClipPlane = distance * 2f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color32(24, 26, 30, 255);
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.transform.SetPositionAndRotation(center - normal * distance,
                    Quaternion.LookRotation(normal, canvas.transform.up));
                camera.targetTexture = target;
                Require(target.Create(), "Could not allocate the graphics panel capture");
                camera.Render();

                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
                image.Apply();
                var pixels = image.GetPixels32();
                var background = pixels[0];
                int distinct = pixels.Count(pixel => Mathf.Abs(pixel.r - background.r) > 24 ||
                    Mathf.Abs(pixel.g - background.g) > 24 || Mathf.Abs(pixel.b - background.b) > 24);
                Require(distinct > pixels.Length / 100, "The graphics panel capture appears empty");
                File.WriteAllBytes(Path.Combine(output, filename), image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousTarget;
                camera.targetTexture = null;
                Destroy(camera.gameObject);
                target.Release();
                Destroy(target);
                Destroy(image);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private void Finish(Exception error)
        {
            string phase = verifySaved ? "restart" : "interaction";
            if (error != null)
            {
                failed = true;
                File.WriteAllText(Path.Combine(output, phase + "-failed.txt"), error.ToString());
                Debug.LogError("GRAPHICS_OPTIONS_WITNESS_FAILED " + error);
                Application.Quit(2);
                return;
            }
            File.WriteAllText(Path.Combine(output, phase + "-complete.txt"), "Graphics Options witness passed");
            Application.Quit();
        }
    }
}
