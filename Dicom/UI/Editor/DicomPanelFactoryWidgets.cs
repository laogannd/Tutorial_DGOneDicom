using TMPro;
using UnityEngine;
using UnityEngine.UI;

using VRQuestion;

namespace Dicom.UI.EditorTools
{
    // DicomPanelFactory 的 UGUI 控件构建 helper：标签/滑块/按钮/开关
    // 可点击控件统一挂 UIPokeBridge，兼容 HandCanvasPointer 射线与手指触碰
    public static partial class DicomPanelFactory
    {
        const float RowHeight = 44f;
        const float LabelHeight = 30f;
        static readonly Color PanelBg = new Color(0.1f, 0.12f, 0.16f, 0.95f);
        static readonly Color SliderBg = new Color(0.2f, 0.22f, 0.28f, 1f);
        static readonly Color Accent = new Color(0.3f, 0.7f, 1f, 1f);
        static readonly Color ButtonBg = new Color(0.25f, 0.45f, 0.7f, 1f);

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

        // 创建一行文本标签，带 LayoutElement 固定高度
        static TextMeshProUGUI CreateLabel(string name, Transform parent, string text, float fontSize, float height)
        {
            var rt = CreateRect(name, parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.enableWordWrapping = true;
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return tmp;
        }

        // 标题
        static TextMeshProUGUI CreateHeader(string name, Transform parent, string text)
        {
            var tmp = CreateLabel(name, parent, text, 22f, 34f);
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Accent;
            return tmp;
        }

        // 创建水平滑块，把手挂 UIPokeBridge；返回 Slider
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
            Stretch(fillArea, new Vector2(0f, 0.25f), new Vector2(1f, 0.75f));
            var fill = CreateImage("Fill", fillArea, Accent);
            Stretch((RectTransform)fill.transform, Vector2.zero, Vector2.one);

            // 把手
            var handleArea = CreateRect("Handle Slide Area", rt);
            Stretch(handleArea, Vector2.zero, Vector2.one);
            var handle = CreateImage("Handle", handleArea, Color.white);
            var handleRt = (RectTransform)handle.transform;
            handleRt.sizeDelta = new Vector2(30f, 0f);
            Stretch(handleRt, new Vector2(0f, 0f), new Vector2(0f, 1f));

            slider.fillRect = (RectTransform)fill.transform;
            slider.handleRect = handleRt;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;

            // 手指可推：PokeSlider 自带碰撞+HandTouchEvent，把触碰位置投影到轨道；射线扣扳机仍可拖动
            rt.gameObject.AddComponent<PokeSlider>();
            return slider;
        }

        // 创建按钮，挂 UIPokeBridge；返回 Button
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

            var label = CreateLabel("Text", rt, text, 20f, RowHeight);
            label.alignment = TextAlignmentOptions.Center;
            Stretch((RectTransform)label.transform, Vector2.zero, Vector2.one);

            rt.gameObject.AddComponent<UIPokeBridge>();
            return btn;
        }

        // 创建开关，勾选框挂 UIPokeBridge；返回 Toggle
        static Toggle CreateToggle(string name, Transform parent, string text)
        {
            var rt = CreateRect(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;

            var toggle = rt.gameObject.AddComponent<Toggle>();

            var box = CreateImage("Background", rt, SliderBg);
            var boxRt = (RectTransform)box.transform;
            boxRt.anchorMin = new Vector2(0f, 0.5f);
            boxRt.anchorMax = new Vector2(0f, 0.5f);
            boxRt.sizeDelta = new Vector2(32f, 32f);
            boxRt.anchoredPosition = new Vector2(20f, 0f);

            var check = CreateImage("Checkmark", box.transform, Accent);
            Stretch((RectTransform)check.transform, new Vector2(0.15f, 0.15f), new Vector2(0.85f, 0.85f));

            var label = CreateLabel("Label", rt, text, 20f, RowHeight);
            var labelRt = (RectTransform)label.transform;
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.offsetMin = new Vector2(60f, 0f);
            labelRt.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.Left;

            toggle.targetGraphic = box;
            toggle.graphic = check;
            toggle.gameObject.AddComponent<UIPokeBridge>();
            return toggle;
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
