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

            try
            {
                manager.SetScreen(0);
                var graphics = manager.transform.GetComponentsInChildren<FVRPointableButton>()
                    .Single(control => control.name == "Button_Option_3_Graphics");
                graphics.Button.onClick.Invoke();

                var nativePage = manager.OPS_Quality.transform.Find("Canvas/Native Graphics Settings");
                Require(nativePage, "Native graphics did not attach when the late-created panel initialized");
                CheckPanel(manager, hand, graphics, nativePage);
                Finish(null);
            }
            catch (Exception error)
            {
                Finish(error);
            }
        }

        private void CheckPanel(OptionsPanel_Screenmanager manager, FVRViveHand hand,
            FVRPointableButton graphics, Transform nativePage)
        {
            var quality = manager.OPS_Quality.transform;
            Require(nativePage.gameObject.activeInHierarchy, "Stock Graphics Options button did not open the native page");

            var canvas = nativePage.GetComponentInParent<Canvas>(true);
            var renderScale = nativePage.Find("RenderScale Value").GetComponent<Text>();
            var preset = nativePage.Find("Preset Value").GetComponent<Text>();
            var exposure = nativePage.Find("Exposure Value").GetComponent<Text>();
            var expectedExposure = Path.Combine(output, "exposure.txt");
            var expectedPath = Path.Combine(output, "render-scale.txt");
            var stateDirectory = Environment.GetEnvironmentVariable("NATIVEH3VR_STATE_DIR");
            Require(!string.IsNullOrEmpty(stateDirectory), "Graphics Options witness requires isolated launcher state");
            string settingsPath = Path.Combine(stateDirectory, "graphics-options.json");
            if (verifySaved)
            {
                Require(File.Exists(settingsPath), "No isolated graphics preferences after restart");
                Require(renderScale.text == File.ReadAllText(expectedPath), "Render scale was not restored after restart");
                Require(preset.text == "Custom", "Custom preset state was not restored after restart");
                Require(exposure.text == File.ReadAllText(expectedExposure), "Exposure was not restored after restart");
                return;
            }

            Require(!File.Exists(settingsPath) && preset.text == "Balanced" && renderScale.text == "100%" &&
                    exposure.text == "+0.25 EV",
                "A fresh state directory did not start at Balanced, 100% and +0.25 EV");
            string initialPreset = preset.text;
            nativePage.Find("Preset Next").GetComponent<Button>().onClick.Invoke();
            Require(preset.text != initialPreset && preset.text != "Custom", "Selecting a named preset did not update the panel");
            string initialScale = renderScale.text;
            var scaleNext = nativePage.Find("RenderScale Next").GetComponent<FVRPointableButton>();
            var collider = scaleNext.GetComponent<BoxCollider>();
            Physics.SyncTransforms();
            var origin = collider.bounds.center - scaleNext.transform.forward * 0.025f;
            var input = hand.Input;
            var previous = hand.CurrentPointable;
            try
            {
                Require(Physics.Raycast(origin, scaleNext.transform.forward, out var hit,
                            Mathf.Min(0.09f, scaleNext.MaxPointingRange), hand.PointingLayerMask, QueryTriggerInteraction.Collide) &&
                        hit.collider == collider, "A stock controller ray did not hit the new render-scale pointable");
                hand.Input.TriggerDown = true;
                hand.CurrentPointable = hit.collider.GetComponent<FVRPointable>();
            }
            finally
            {
                hand.Input = input;
                hand.CurrentPointable = previous;
            }
            Require(renderScale.text != initialScale, "Pointing at and triggering the render-scale control did not change it");
            Require(preset.text == "Custom", "Changing render scale did not select Custom");
            Require(exposure.text == "+0.5 EV", "The Quality preset did not apply its exposure");
            nativePage.Find("Exposure Next").GetComponent<Button>().onClick.Invoke();
            Require(exposure.text == "+0.75 EV", "The exposure arrow did not change the compensation");

            var back = quality.Find("Canvas/Button_Back (3)").GetComponent<Button>();
            var panelForward = back.transform.forward;
            CapturePage(canvas, panelForward, "native-graphics.png");

            var tab = quality.Find("Canvas/Graphics Page Navigation/GraphicsPage").GetComponent<Button>();
            var gamePage = quality.Find("Canvas/Stock Game Graphics Settings");
            tab.onClick.Invoke();
            Require(gamePage.gameObject.activeInHierarchy && !nativePage.gameObject.activeInHierarchy,
                "Game Settings did not display the retained stock controls");
            CapturePage(canvas, panelForward, "game-graphics.png");
            tab.onClick.Invoke();
            Require(nativePage.gameObject.activeInHierarchy, "Native Graphics did not reopen");

            tab.onClick.Invoke();
            back.onClick.Invoke();
            Require(!quality.gameObject.activeInHierarchy, "Stock Back did not return to options navigation");
            graphics.Button.onClick.Invoke();
            Require(nativePage.gameObject.activeInHierarchy && renderScale.text != initialScale && preset.text == "Custom",
                "Reopening Graphics Options lost the selected settings");
            Require(File.Exists(settingsPath), "Graphics controls did not write to isolated launcher state");
            File.WriteAllText(expectedPath, renderScale.text);
            File.WriteAllText(expectedExposure, exposure.text);
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
