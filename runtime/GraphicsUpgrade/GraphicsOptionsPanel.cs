using System;
using System.Collections.Generic;
using FistVR;
using UnityEngine;
using UnityEngine.UI;

namespace NativeH3VR.GraphicsUpgrade
{
    internal sealed class GraphicsOptionsPanel : MonoBehaviour
    {
        // The converter adds this field to the staged game assembly before Unity imports it.
        private const string CallbackName = "NativeH3VRGraphicsScreenInitialized";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            var field = typeof(OptionsScreen_Quality).GetField(CallbackName);
            if (field == null)
                throw new MissingFieldException(typeof(OptionsScreen_Quality).FullName, CallbackName);
            field.SetValue(null, (Action<OptionsScreen_Quality>)Attach);
        }

        private static void Attach(OptionsScreen_Quality screen)
        {
            if (!screen.GetComponent<GraphicsOptionsPanel>())
                screen.gameObject.AddComponent<GraphicsOptionsPanel>().Build(screen);
        }

        private static readonly GraphicsSetting[] RenderSettings =
        {
            GraphicsSetting.Preset,
            GraphicsSetting.RenderScale,
            GraphicsSetting.Msaa,
            GraphicsSetting.ShadowQuality,
            GraphicsSetting.ShadowDistance,
            GraphicsSetting.Exposure
        };

        private static readonly GraphicsSetting[] ImageSettings =
        {
            GraphicsSetting.AdditionalLights,
            GraphicsSetting.AmbientOcclusion,
            GraphicsSetting.Hdr,
            GraphicsSetting.Bloom,
            GraphicsSetting.Tonemapping,
            GraphicsSetting.Sharpening
        };

        private const float TextScale = 0.07f;
        private readonly Dictionary<GraphicsSetting, Text> values = new Dictionary<GraphicsSetting, Text>();
        private RectTransform nativePage;
        private RectTransform gamePage;
        private Text stockTitle;
        private string stockTitleText;
        private Text tabLabel;

        private void Build(OptionsScreen_Quality screen)
        {
            var stockQuality = screen.OBS_CurQualitySetting;
            if (!stockQuality || stockQuality.ButtonsInSet == null || stockQuality.ButtonsInSet.Length == 0)
                return;

            var canvas = stockQuality.GetComponentInParent<Canvas>(true);
            if (!canvas || !(canvas.transform is RectTransform parent))
                return;

            var template = stockQuality.ButtonsInSet[0];
            if (!template || !template.TryGetComponent(out Button _) ||
                !template.TryGetComponent(out Image _) || !template.TryGetComponent(out BoxCollider _) || !template.GetComponentInChildren<Text>(true))
                return;

            // Keep the stock title and back control in their original positions.
            stockTitle = parent.Find("Label_Title")?.GetComponent<Text>();
            if (!stockTitle)
                return;
            stockTitleText = stockTitle.text;

            nativePage = Page(parent, "Native Graphics Settings");
            gamePage = Page(parent, "Stock Game Graphics Settings");
            var superseded = Page(parent, "Superseded Built-in Graphics Controls");

            // InitScreen still refreshes these stock components. Keep their references and their
            // activeSelf flags intact while their parent determines which controls are visible.
            Move(stockQuality, gamePage);
            Move(screen.OBS_Custom_MSAA, gamePage);
            Move(screen.OBS_Custom_TextureFiltering, gamePage);
            Move(screen.OBS_Custom_ParticleFXColQuality, gamePage);
            Move(screen.OBS_Post_CC, superseded);
            Move(screen.OBS_Post_Bloom, superseded);
            Move(screen.OBS_Post_AO, superseded);
            Move(screen.OBS_Custom_Shadows, superseded);
            Move(screen.OBS_Custom_ShadowResolution, superseded);
            Move(screen.OBS_Custom_ShadowDistance, superseded);
            Move(screen.OBS_Custom_ShadowCascades, superseded);
            Move(screen.OBS_Custom_SoftParticles, superseded);
            Move(screen.OBS_Custom_MaxPixelLights, superseded);

            if (screen.OBS_Custom_MSAA && screen.OBS_Custom_MSAA.TryGetComponent(out Text scopeLabel))
                scopeLabel.text = "Scope MSAA";

            // Construct inactive so the cloned pointables initialize with our colors and listeners.
            var navigation = Page(parent, "Graphics Page Navigation");
            tabLabel = CreateButton(template, navigation, "GraphicsPage", "Game Settings",
                new Vector2(21.8f, 22.07f), 8.6f, 2.8f, TogglePage);
            AddColumn(template, nativePage, RenderSettings, 1f);
            AddColumn(template, nativePage, ImageSettings, 19f);
            navigation.gameObject.SetActive(true);
            ShowGamePage(false);
        }

        private void OnEnable()
        {
            GraphicsOptions.Changed += RefreshValues;
            if (nativePage)
                ShowGamePage(false);
        }

        private void OnDisable()
        {
            GraphicsOptions.Changed -= RefreshValues;
        }

        private void AddColumn(FVRPointableButton template, RectTransform page, GraphicsSetting[] settings, float x)
        {
            for (int row = 0; row < settings.Length; row++)
            {
                GraphicsSetting setting = settings[row];
                float y = 19f - 3.24f * row;
                Text label = CreateText(page, setting + " Label", GraphicsOptions.Label(setting),
                    new Vector2(x + 8f, y), new Vector2(16f, 1.5f), TextAnchor.MiddleLeft);
                label.fontStyle = FontStyle.Bold;

                values[setting] = CreateText(page, setting + " Value", "",
                    new Vector2(x + 8f, y - 1.58f), new Vector2(9.7f, 2.1f), TextAnchor.MiddleCenter);
                CreateButton(template, page, setting + " Previous", "<",
                    new Vector2(x + 1.5f, y - 1.58f), 3f, 1.8f, () => Cycle(setting, -1));
                CreateButton(template, page, setting + " Next", ">",
                    new Vector2(x + 14.5f, y - 1.58f), 3f, 1.8f, () => Cycle(setting, 1));
            }
        }

        private static void Cycle(GraphicsSetting setting, int step)
        {
            int count = GraphicsOptions.ChoiceCount(setting);
            int index = GraphicsOptions.GetIndex(setting);
            if (index >= count)
                index = step > 0 ? -1 : count;
            GraphicsOptions.SetIndex(setting, (index + step + count) % count);
        }

        private void TogglePage()
        {
            ShowGamePage(!gamePage.gameObject.activeSelf);
        }

        private void ShowGamePage(bool showGame)
        {
            nativePage.gameObject.SetActive(!showGame);
            gamePage.gameObject.SetActive(showGame);
            stockTitle.text = showGame ? "Game Graphics" : stockTitleText;
            tabLabel.text = showGame ? "Graphics" : "Game Settings";
            RefreshValues();
        }

        private void RefreshValues()
        {
            foreach (var item in values)
                item.Value.text = GraphicsOptions.ChoiceLabel(item.Key, GraphicsOptions.GetIndex(item.Key));
        }

        private static RectTransform Page(RectTransform parent, string name)
        {
            var page = new GameObject(name, typeof(RectTransform));
            page.SetActive(false);
            var rect = (RectTransform)page.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static void Move(OptionsPanel_ButtonSet set, RectTransform page)
        {
            if (set)
                set.transform.SetParent(page, false);
        }

        private Text CreateText(RectTransform parent, string name, string content, Vector2 position,
            Vector2 size, TextAnchor alignment)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Text));
            var text = obj.GetComponent<Text>();
            text.rectTransform.SetParent(parent, false);
            Place(text.rectTransform, position, size);
            text.font = stockTitle.font;
            text.fontSize = 18;
            text.color = stockTitle.color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.text = content;
            return text;
        }

        private static Text CreateButton(FVRPointableButton template, RectTransform parent, string name,
            string content, Vector2 position, float width, float height, UnityEngine.Events.UnityAction action)
        {
            var pointable = Instantiate(template, parent, false);
            pointable.name = name;
            var rect = (RectTransform)pointable.transform;
            Place(rect, position, new Vector2(width, height));

            var collider = pointable.GetComponent<BoxCollider>();
            collider.size = new Vector3(rect.sizeDelta.x, rect.sizeDelta.y, collider.size.z);
            collider.center = new Vector3(0f, 0f, collider.center.z);
            pointable.DoesRepeat = false;
            pointable.ColorUnselected = new Color(0.15f, 0.20f, 0.24f, 1f);
            pointable.ColorSelected = new Color(0.29f, 0.42f, 0.53f, 1f);
            pointable.Image = pointable.GetComponent<Image>();
            pointable.Button = pointable.GetComponent<Button>();
            pointable.Button.onClick = new Button.ButtonClickedEvent();
            pointable.Button.onClick.AddListener(action);

            var text = pointable.GetComponentInChildren<Text>(true);
            text.text = content;
            text.fontSize = 18;
            return text;
        }

        private static void Place(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localRotation = Quaternion.identity;
            rect.localScale = new Vector3(TextScale, TextScale, TextScale);
            rect.anchoredPosition3D = new Vector3(position.x, position.y, 0f);
            rect.sizeDelta = size / TextScale;
        }
    }
}
