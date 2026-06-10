using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Dicom.UI.EditorTools
{
    // DicomPanelFactory 的内容组装与字段绑定
    public static partial class DicomPanelFactory
    {
        // 搭建过程中收集的控件引用，统一回填到 DicomPanelUI 序列化字段
        class PanelRefs
        {
            public TextMeshProUGUI StatusText;
            public Image ProgressFill;
            public Slider PointSize, WindowCenter, WindowWidth, Gain, TintR, TintG, TintB;
            public TextMeshProUGUI PointSizeLabel, WindowCenterLabel, WindowWidthLabel, GainLabel;
            public Slider ThresholdMin, ThresholdMax, NormalizeMin, NormalizeMax;
            public TextMeshProUGUI ThresholdMinLabel, ThresholdMaxLabel, NormalizeMinLabel, NormalizeMaxLabel;
            public Button ApplyThreshold, ApplyNormalize;
            public Toggle ClipToggle, ClassColorToggle, LutColorToggle, BreakpointColorToggle;
            public Button LutPresetButton;
            public TextMeshProUGUI LutPresetLabel;
            public TextMeshProUGUI HuRangeText;
            public Button ApplyHuRangeButton;
            public TextMeshProUGUI HuApplyHintLabel;
        }

        static PanelRefs BuildSections(RectTransform content)
        {
            var r = new PanelRefs();

            CreateHeader("TitleHeader", content, "DICOM 操作面板");

            // 状态区
            CreateHeader("StatusHeader", content, "加载状态");
            r.StatusText = CreateLabel("StatusText", content, "阶段: 空闲", 18f, 140f);
            r.ProgressFill = CreateProgressBar(content);

            // 外观区
            CreateHeader("AppearanceHeader", content, "外观 (实时)");
            r.PointSizeLabel = CreateLabel("PointSizeLabel", content, "点大小", 18f, LabelHeight);
            r.PointSize = CreateSlider("PointSizeSlider", content);
            r.WindowCenterLabel = CreateLabel("WindowCenterLabel", content, "窗位", 18f, LabelHeight);
            r.WindowCenter = CreateSlider("WindowCenterSlider", content);
            r.WindowWidthLabel = CreateLabel("WindowWidthLabel", content, "窗宽", 18f, LabelHeight);
            r.WindowWidth = CreateSlider("WindowWidthSlider", content);
            r.GainLabel = CreateLabel("GainLabel", content, "增益", 18f, LabelHeight);
            r.Gain = CreateSlider("GainSlider", content);
            CreateLabel("TintLabel", content, "色调 R / G / B", 18f, LabelHeight);
            r.TintR = CreateSlider("TintRSlider", content);
            r.TintG = CreateSlider("TintGSlider", content);
            r.TintB = CreateSlider("TintBSlider", content);

            // 点生成区
            CreateHeader("RebuildHeader", content, "点生成 (需 Apply)");
            r.ThresholdMinLabel = CreateLabel("ThresholdMinLabel", content, "阈值下限", 18f, LabelHeight);
            r.ThresholdMin = CreateSlider("ThresholdMinSlider", content);
            r.ThresholdMaxLabel = CreateLabel("ThresholdMaxLabel", content, "阈值上限", 18f, LabelHeight);
            r.ThresholdMax = CreateSlider("ThresholdMaxSlider", content);
            r.NormalizeMinLabel = CreateLabel("NormalizeMinLabel", content, "归一化下限", 18f, LabelHeight);
            r.NormalizeMin = CreateSlider("NormalizeMinSlider", content);
            r.NormalizeMaxLabel = CreateLabel("NormalizeMaxLabel", content, "归一化上限", 18f, LabelHeight);
            r.NormalizeMax = CreateSlider("NormalizeMaxSlider", content);
            r.ApplyThreshold = CreateButton("ApplyThresholdButton", content, "Apply 阈值");
            r.ApplyNormalize = CreateButton("ApplyNormalizeButton", content, "Apply 归一化");

            // 开关区
            CreateHeader("ToggleHeader", content, "开关");
            r.ClipToggle = CreateToggle("ClipToggle", content, "启用裁切平面");
            r.ClassColorToggle = CreateToggle("ClassColorToggle", content, "按标签分类着色");
            r.LutColorToggle = CreateToggle("LutColorToggle", content, "离散 LUT 伪彩");
            r.BreakpointColorToggle = CreateToggle("BreakpointColorToggle", content, "断点插值显色");
            r.LutPresetLabel = CreateLabel("LutPresetLabel", content, "LUT 预设: HotIron", 18f, LabelHeight);
            r.LutPresetButton = CreateButton("LutPresetButton", content, "切换 LUT 预设");

            // HU 区间分析区:加载后自动统计的占用区间列表 + 一键写入分类配置
            CreateHeader("HuRangeHeader", content, "HU 区间分析");
            r.HuRangeText = CreateLabel("HuRangeText", content, "加载完成后自动统计", 18f, 160f);
            r.ApplyHuRangeButton = CreateButton("ApplyHuRangeButton", content, "一键应用到 Profile");
            r.HuApplyHintLabel = CreateLabel("HuApplyHintLabel", content, "", 16f, LabelHeight);

            return r;
        }

        // 进度条：底槽 + 左对齐 fillAmount 填充
        static Image CreateProgressBar(RectTransform parent)
        {
            var slot = CreateImage("ProgressBar", parent, SliderBg);
            var le = slot.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 20f;
            le.preferredHeight = 20f;

            var fill = CreateImage("Fill", slot.transform, Accent);
            Stretch((RectTransform)fill.transform, Vector2.zero, Vector2.one);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            return fill;
        }

        // 用 SerializedObject 写私有序列化字段，避免反射(照搬 XRay 工厂做法)
        static void BindPanel(DicomPanelUI panel, PanelRefs r)
        {
            var so = new SerializedObject(panel);
            Set(so, "_statusText", r.StatusText);
            Set(so, "_progressFill", r.ProgressFill);
            Set(so, "_pointSizeSlider", r.PointSize);
            Set(so, "_pointSizeLabel", r.PointSizeLabel);
            Set(so, "_windowCenterSlider", r.WindowCenter);
            Set(so, "_windowCenterLabel", r.WindowCenterLabel);
            Set(so, "_windowWidthSlider", r.WindowWidth);
            Set(so, "_windowWidthLabel", r.WindowWidthLabel);
            Set(so, "_gainSlider", r.Gain);
            Set(so, "_gainLabel", r.GainLabel);
            Set(so, "_tintRSlider", r.TintR);
            Set(so, "_tintGSlider", r.TintG);
            Set(so, "_tintBSlider", r.TintB);
            Set(so, "_thresholdMinSlider", r.ThresholdMin);
            Set(so, "_thresholdMinLabel", r.ThresholdMinLabel);
            Set(so, "_thresholdMaxSlider", r.ThresholdMax);
            Set(so, "_thresholdMaxLabel", r.ThresholdMaxLabel);
            Set(so, "_normalizeMinSlider", r.NormalizeMin);
            Set(so, "_normalizeMinLabel", r.NormalizeMinLabel);
            Set(so, "_normalizeMaxSlider", r.NormalizeMax);
            Set(so, "_normalizeMaxLabel", r.NormalizeMaxLabel);
            Set(so, "_applyThresholdButton", r.ApplyThreshold);
            Set(so, "_applyNormalizeButton", r.ApplyNormalize);
            Set(so, "_clipToggle", r.ClipToggle);
            Set(so, "_classColorToggle", r.ClassColorToggle);
            Set(so, "_lutColorToggle", r.LutColorToggle);
            Set(so, "_breakpointColorToggle", r.BreakpointColorToggle);
            Set(so, "_lutPresetButton", r.LutPresetButton);
            Set(so, "_lutPresetLabel", r.LutPresetLabel);
            Set(so, "_huRangeText", r.HuRangeText);
            Set(so, "_applyHuRangeButton", r.ApplyHuRangeButton);
            Set(so, "_huApplyHintLabel", r.HuApplyHintLabel);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Set(SerializedObject so, string field, Object value)
        {
            var prop = so.FindProperty(field);
            if (prop != null) prop.objectReferenceValue = value;
        }

        static void EnsureDir(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return;
            string parent = System.IO.Path.GetDirectoryName(dir).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(dir);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureDir(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
