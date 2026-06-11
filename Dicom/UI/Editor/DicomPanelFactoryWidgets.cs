using TMPro;
using UnityEngine;
using UnityEngine.UI;

using VRQuestion;

namespace Dicom.UI.EditorTools
{
    // DicomPanelFactory 的 UGUI 控件构建 helper:标签/滑块/按钮/开关/折叠分区
    // 可点击控件统一挂 UIPokeBridge,兼容 HandCanvasPointer 射线与手指触碰
    // 医疗深色配色 + 加大命中目标 + 按下/hover 视觉反馈,适配 VR 手指戳
    public static partial class DicomPanelFactory
    {
        // VR 手指戳命中目标加大:行高从 44 提到 64,标签略增
        const float RowHeight = 64f;
        const float LabelHeight = 34f;
        const float HandleWidth = 56f;   // 滑块把手加宽,易戳中
        const float CheckBoxSize = 48f;   // 勾选框加大

        // 医疗专业深色:近黑底 + 青色强调,降低眩光,高对比便于读数
        static readonly Color PanelBg = new Color(0.07f, 0.09f, 0.11f, 0.97f);
        static readonly Color CardBg = new Color(0.12f, 0.15f, 0.18f, 1f);          // 分区卡片底
        static readonly Color HeaderBg = new Color(0.16f, 0.20f, 0.24f, 1f);        // 分区标题条
        static readonly Color SliderBg = new Color(0.18f, 0.21f, 0.25f, 1f);        // 滑槽底
        static readonly Color Accent = new Color(0.18f, 0.78f, 0.85f, 1f);          // 青色强调(填充/勾选)
        static readonly Color AccentDim = new Color(0.14f, 0.55f, 0.60f, 1f);       // 强调暗色(hover)
        static readonly Color ButtonBg = new Color(0.16f, 0.42f, 0.52f, 1f);        // 按钮常态
        static readonly Color ButtonHover = new Color(0.22f, 0.56f, 0.68f, 1f);     // 按钮 hover
        static readonly Color ButtonPressed = new Color(0.30f, 0.72f, 0.82f, 1f);   // 按钮按下高亮
        static readonly Color HandleColor = new Color(0.95f, 0.97f, 0.98f, 1f);     // 把手
        static readonly Color TextPrimary = new Color(0.92f, 0.95f, 0.96f, 1f);
        static readonly Color TextMuted = new Color(0.62f, 0.68f, 0.72f, 1f);

        static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        static Image CreateImage(string name, Transform parent, Color color)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            return img;
        }

        // 创建一行文本标签,带 LayoutElement 固定高度
        static TextMeshProUGUI CreateLabel(string name, Transform parent, string text, float fontSize, float height)
        {
            var rt = CreateRect(name, parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = TextPrimary;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.enableWordWrapping = true;
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return tmp;
        }

        // 段内小标题(分区内的子标签,如"色调 R/G/B")
        static TextMeshProUGUI CreateHeader(string name, Transform parent, string text)
        {
            var tmp = CreateLabel(name, parent, text, 24f, 38f);
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Accent;
            return tmp;
        }

        // 创建水平滑块:加宽把手 + 圆润填充;PokeSlider 自带碰撞,射线扣扳机也可拖
        static Slider CreateSlider(string name, Transform parent)
        {
            var rt = CreateRect(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;

            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = SliderBg;

            var slider = rt.gameObject.AddComponent<Slider>();

            // 填充区
            var fillArea = CreateRect("Fill Area", rt);
            Stretch(fillArea, new Vector2(0f, 0.3f), new Vector2(1f, 0.7f));
            var fill = CreateImage("Fill", fillArea, Accent);
            Stretch((RectTransform)fill.transform, Vector2.zero, Vector2.one);

            // 把手:加宽到 56,手指更易戳中
            var handleArea = CreateRect("Handle Slide Area", rt);
            Stretch(handleArea, Vector2.zero, Vector2.one);
            var handle = CreateImage("Handle", handleArea, HandleColor);
            var handleRt = (RectTransform)handle.transform;
            handleRt.sizeDelta = new Vector2(HandleWidth, 0f);
            Stretch(handleRt, new Vector2(0f, 0f), new Vector2(0f, 1f));

            slider.fillRect = (RectTransform)fill.transform;
            slider.handleRect = handleRt;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            // 把手 hover/按下变青,反馈手指接触
            slider.transition = Selectable.Transition.ColorTint;
            slider.colors = MakeColorBlock(HandleColor, Accent, ButtonPressed);

            // 手指可推:PokeSlider 自带碰撞 + HandTouchEvent,投影触碰位置到轨道;射线扣扳机仍可拖
            rt.gameObject.AddComponent<PokeSlider>();
            return slider;
        }

        // 创建按钮:加高 + hover/按下高亮的 ColorBlock 反馈
        static Button CreateButton(string name, Transform parent, string text)
        {
            var rt = CreateRect(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;

            var img = rt.gameObject.AddComponent<Image>();
            img.color = ButtonBg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            // 戳中/hover 时按钮变亮,松开复位,明确反馈点击
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = MakeColorBlock(ButtonBg, ButtonHover, ButtonPressed);

            var label = CreateLabel("Text", rt, text, 24f, RowHeight);
            label.alignment = TextAlignmentOptions.Center;
            label.fontStyle = FontStyles.Bold;
            Stretch((RectTransform)label.transform, Vector2.zero, Vector2.one);

            rt.gameObject.AddComponent<UIPokeBridge>();
            return btn;
        }

        // 创建开关:加大勾选框 + hover 反馈,整行可戳
        static Toggle CreateToggle(string name, Transform parent, string text)
        {
            var rt = CreateRect(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;

            // 整行底色,手指戳行任意处都命中
            var rowBg = rt.gameObject.AddComponent<Image>();
            rowBg.color = SliderBg;

            var toggle = rt.gameObject.AddComponent<Toggle>();

            var box = CreateImage("Background", rt, HeaderBg);
            var boxRt = (RectTransform)box.transform;
            boxRt.anchorMin = new Vector2(0f, 0.5f);
            boxRt.anchorMax = new Vector2(0f, 0.5f);
            boxRt.sizeDelta = new Vector2(CheckBoxSize, CheckBoxSize);
            boxRt.anchoredPosition = new Vector2(36f, 0f);

            var check = CreateImage("Checkmark", box.transform, Accent);
            Stretch((RectTransform)check.transform, new Vector2(0.18f, 0.18f), new Vector2(0.82f, 0.82f));

            var label = CreateLabel("Label", rt, text, 24f, RowHeight);
            var labelRt = (RectTransform)label.transform;
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.offsetMin = new Vector2(80f, 0f);
            labelRt.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.Left;

            toggle.targetGraphic = box;
            toggle.graphic = check;
            // 勾选框 hover/按下变亮
            toggle.transition = Selectable.Transition.ColorTint;
            toggle.colors = MakeColorBlock(HeaderBg, AccentDim, Accent);
            toggle.gameObject.AddComponent<UIPokeBridge>();
            return toggle;
        }

        // 统一的 Selectable 颜色状态:常态/hover/按下,选中沿用 hover,高 fade 让反馈跟手
        static ColorBlock MakeColorBlock(Color normal, Color highlighted, Color pressed)
        {
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = normal;
            cb.highlightedColor = highlighted;
            cb.pressedColor = pressed;
            cb.selectedColor = highlighted;
            cb.disabledColor = new Color(normal.r, normal.g, normal.b, 0.4f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.06f;
            return cb;
        }

        static void Stretch(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
