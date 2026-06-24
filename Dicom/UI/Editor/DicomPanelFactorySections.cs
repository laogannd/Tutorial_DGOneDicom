using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Dicom.UI;
using VRQuestion;

namespace Dicom.UI.EditorTools
{
    // DicomPanelFactory 的内容组装与字段绑定
    // 各功能组包进可折叠分区卡片,header 可手指戳折叠/展开,减少长列表滚动误触
    public static partial class DicomPanelFactory
    {
        // 搭建过程中收集的控件引用,统一回填到 DicomPanelUI 序列化字段
        class PanelRefs
        {
            public TextMeshProUGUI StatusText;
            public Image ProgressFill;
            public Slider PointSize, WindowCenter, WindowWidth, Gain, TintR, TintG, TintB;
            public TextMeshProUGUI PointSizeLabel, WindowCenterLabel, WindowWidthLabel, GainLabel;
            public Slider ThresholdMin, ThresholdMax, NormalizeMin, NormalizeMax;
            public TextMeshProUGUI ThresholdMinLabel, ThresholdMaxLabel, NormalizeMinLabel, NormalizeMaxLabel;
            public Button ApplyThreshold, ApplyNormalize;
            public Button ReconstructAxisButton, RebuildButton;
            public TextMeshProUGUI ReconstructAxisLabel;
            public Toggle ClipToggle, ClassColorToggle, LutColorToggle, BreakpointColorToggle;
            public Button SpawnClipButton, ClearClipButton;
            public Button LutPresetButton;
            public TextMeshProUGUI LutPresetLabel;
            public Slider ModelScale;
            public TextMeshProUGUI ModelScaleLabel;
            public Button ResetTransformButton;
            public TextMeshProUGUI HuRangeText;
            public Button ApplyHuRangeButton;
            public TextMeshProUGUI HuApplyHintLabel;
        }

        static PanelRefs BuildSections(RectTransform content)
        {
            var r = new PanelRefs();

            // 顶部主标题条由 BuildPanel 固定在面板顶部,不进滚动区

            // 状态区:默认展开,加载时需可见
            var status = CreateSection(content, "加载状态", true);
            r.StatusText = CreateLabel("StatusText", status, "阶段: 空闲", 20f, 150f);
            r.ProgressFill = CreateProgressBar(status);

            // 外观区:实时生效
            var appearance = CreateSection(content, "外观 (实时)", true);
            r.PointSizeLabel = CreateLabel("PointSizeLabel", appearance, "点大小", 20f, LabelHeight);
            r.PointSize = CreateSlider("PointSizeSlider", appearance);
            r.WindowCenterLabel = CreateLabel("WindowCenterLabel", appearance, "窗位", 20f, LabelHeight);
            r.WindowCenter = CreateSlider("WindowCenterSlider", appearance);
            r.WindowWidthLabel = CreateLabel("WindowWidthLabel", appearance, "窗宽", 20f, LabelHeight);
            r.WindowWidth = CreateSlider("WindowWidthSlider", appearance);
            r.GainLabel = CreateLabel("GainLabel", appearance, "增益", 20f, LabelHeight);
            r.Gain = CreateSlider("GainSlider", appearance);
            CreateLabel("TintLabel", appearance, "色调 R / G / B", 20f, LabelHeight);
            r.TintR = CreateSlider("TintRSlider", appearance);
            r.TintG = CreateSlider("TintGSlider", appearance);
            r.TintB = CreateSlider("TintBSlider", appearance);

            // 点生成区:默认折叠,使用频率较低且需 Apply
            var rebuild = CreateSection(content, "点生成 (需 Apply)", false);
            r.ThresholdMinLabel = CreateLabel("ThresholdMinLabel", rebuild, "阈值下限", 20f, LabelHeight);
            r.ThresholdMin = CreateSlider("ThresholdMinSlider", rebuild);
            r.ThresholdMaxLabel = CreateLabel("ThresholdMaxLabel", rebuild, "阈值上限", 20f, LabelHeight);
            r.ThresholdMax = CreateSlider("ThresholdMaxSlider", rebuild);
            r.NormalizeMinLabel = CreateLabel("NormalizeMinLabel", rebuild, "归一化下限", 20f, LabelHeight);
            r.NormalizeMin = CreateSlider("NormalizeMinSlider", rebuild);
            r.NormalizeMaxLabel = CreateLabel("NormalizeMaxLabel", rebuild, "归一化上限", 20f, LabelHeight);
            r.NormalizeMax = CreateSlider("NormalizeMaxSlider", rebuild);
            r.ApplyThreshold = CreateButton("ApplyThresholdButton", rebuild, "Apply 阈值");
            r.ApplyNormalize = CreateButton("ApplyNormalizeButton", rebuild, "Apply 归一化");

            // 重建方向区:默认展开,切换切片堆叠轴 X/Y/Z + 一键刷新重建
            var axis = CreateSection(content, "重建方向", true);
            r.ReconstructAxisLabel = CreateLabel("ReconstructAxisLabel", axis, "重建方向: Z 轴", 20f, LabelHeight);
            r.ReconstructAxisButton = CreateButton("ReconstructAxisButton", axis, "切换重建方向 X/Y/Z");
            r.RebuildButton = CreateButton("RebuildButton", axis, "刷新重建点云");

            // 裁切平面区:默认展开,运行时生成/清除裁切平面 + 启用开关
            var clip = CreateSection(content, "裁切平面", true);
            r.SpawnClipButton = CreateButton("SpawnClipButton", clip, "生成裁切平面");
            r.ClearClipButton = CreateButton("ClearClipButton", clip, "清除裁切平面");
            r.ClipToggle = CreateToggle("ClipToggle", clip, "启用裁切平面");

            // 显色开关区:默认展开,高频切换
            var toggles = CreateSection(content, "显色模式", true);
            r.ClassColorToggle = CreateToggle("ClassColorToggle", toggles, "按标签分类着色");
            r.LutColorToggle = CreateToggle("LutColorToggle", toggles, "离散 LUT 伪彩");
            r.BreakpointColorToggle = CreateToggle("BreakpointColorToggle", toggles, "断点插值显色");
            r.LutPresetLabel = CreateLabel("LutPresetLabel", toggles, "LUT 预设: HotIron", 20f, LabelHeight);
            r.LutPresetButton = CreateButton("LutPresetButton", toggles, "切换 LUT 预设");

            // 模型变换区:等比缩放滑块 + 一键复位位置/大小
            var transform = CreateSection(content, "模型变换", true);
            r.ModelScaleLabel = CreateLabel("ModelScaleLabel", transform, "模型缩放", 20f, LabelHeight);
            r.ModelScale = CreateSlider("ModelScaleSlider", transform);
            r.ResetTransformButton = CreateButton("ResetTransformButton", transform, "复位位置/大小");

            // HU 区间分析区:默认折叠,加载后自动统计
            var hu = CreateSection(content, "HU 区间分析", false);
            r.HuRangeText = CreateLabel("HuRangeText", hu, "加载完成后自动统计", 20f, 170f);
            r.ApplyHuRangeButton = CreateButton("ApplyHuRangeButton", hu, "一键应用到 Profile");
            r.HuApplyHintLabel = CreateLabel("HuApplyHintLabel", hu, "", 18f, LabelHeight);

            return r;
        }

        // 顶部主标题条:锚定面板顶部固定,不随滚动移动,与抓取碰撞盒区域精确重合
        // 深色底 + 居中粗体 + "可抓取移动"提示,明确告知用户抓这条拖面板
        static void CreateTitleBar(RectTransform panel)
        {
            var bar = CreateImage("TitleBar", panel, HeaderBg);
            // 标题条作为远程射线拖拽抓取区:走 EventSystem 拖拽事件移动面板并实时朝向用户,与手抓物理解耦
            bar.gameObject.AddComponent<DicomPanelRayDragMover>();
            var barRt = (RectTransform)bar.transform;
            barRt.anchorMin = new Vector2(0f, 1f);
            barRt.anchorMax = new Vector2(1f, 1f);
            barRt.pivot = new Vector2(0.5f, 1f);
            barRt.sizeDelta = new Vector2(0f, TitleBarHeight);
            barRt.anchoredPosition = Vector2.zero;

            var label = CreateLabel("Title", bar.transform, "DICOM 操作面板", 30f, TitleBarHeight);
            label.alignment = TextAlignmentOptions.Center;
            label.fontStyle = FontStyles.Bold;
            label.color = Accent;
            Stretch((RectTransform)label.transform, Vector2.zero, Vector2.one);

            // 提示文字:可抓取拖动
            var hint = CreateLabel("DragHint", bar.transform, "≡ 抓住此条移动面板", 18f, 26f);
            hint.alignment = TextAlignmentOptions.Right;
            hint.color = TextMuted;
            var hintRt = (RectTransform)hint.transform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.sizeDelta = new Vector2(-24f, 26f);
            hintRt.anchoredPosition = new Vector2(-12f, 6f);
        }

        // 创建可折叠分区卡片:返回放控件的 body 容器(竖直布局)
        // header 挂 CollapsibleSection(IPointerClickHandler) + UIPokeBridge,手指戳 header 折叠/展开
        static RectTransform CreateSection(RectTransform content, string title, bool expanded)
        {
            // 卡片根:竖直布局,header 在上 body 在下
            var card = CreateRect("Section_" + title, content);
            var cardLayout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            cardLayout.spacing = 2f;
            cardLayout.padding = new RectOffset(0, 0, 0, 0);
            cardLayout.childControlWidth = true;
            cardLayout.childControlHeight = true;
            cardLayout.childForceExpandWidth = true;
            cardLayout.childForceExpandHeight = false;
            var cardImg = card.gameObject.AddComponent<Image>();
            cardImg.color = CardBg;

            // header 行:可点击折叠,带箭头 + 标题
            var header = CreateRect("Header", card);
            var headerLe = header.gameObject.AddComponent<LayoutElement>();
            headerLe.minHeight = 56f;
            headerLe.preferredHeight = 56f;
            var headerImg = header.gameObject.AddComponent<Image>();
            headerImg.color = HeaderBg;
            // header 自身作为可选中元素给 hover/按下反馈(用 Button 承载 ColorBlock)
            var headerBtn = header.gameObject.AddComponent<Button>();
            headerBtn.targetGraphic = headerImg;
            headerBtn.transition = Selectable.Transition.ColorTint;
            headerBtn.colors = MakeColorBlock(HeaderBg, AccentDim, Accent);

            var arrow = CreateLabel("Arrow", header, expanded ? "▼" : "▶", 24f, 56f);
            arrow.alignment = TextAlignmentOptions.Center;
            arrow.color = Accent;
            var arrowRt = (RectTransform)arrow.transform;
            arrowRt.anchorMin = new Vector2(0f, 0f);
            arrowRt.anchorMax = new Vector2(0f, 1f);
            arrowRt.sizeDelta = new Vector2(56f, 0f);
            arrowRt.anchoredPosition = new Vector2(28f, 0f);

            var titleLabel = CreateLabel("Title", header, title, 24f, 56f);
            titleLabel.alignment = TextAlignmentOptions.Left;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.color = TextPrimary;
            var titleRt = (RectTransform)titleLabel.transform;
            titleRt.anchorMin = new Vector2(0f, 0f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.offsetMin = new Vector2(64f, 0f);
            titleRt.offsetMax = Vector2.zero;

            // body 容器:竖直布局承载控件
            var body = CreateRect("Body", card);
            var bodyLayout = body.gameObject.AddComponent<VerticalLayoutGroup>();
            bodyLayout.spacing = 10f;
            bodyLayout.padding = new RectOffset(12, 12, 10, 12);
            bodyLayout.childControlWidth = true;
            bodyLayout.childControlHeight = true;
            bodyLayout.childForceExpandWidth = true;
            bodyLayout.childForceExpandHeight = false;
            body.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 折叠组件 + 手指戳桥接(UIPokeBridge 需 IPointerClickHandler,CollapsibleSection 实现之)
            var section = header.gameObject.AddComponent<CollapsibleSection>();
            section.Bind(body.gameObject, arrow, expanded);
            header.gameObject.AddComponent<UIPokeBridge>();

            return body;
        }

        // 进度条:底槽 + 左对齐 fillAmount 填充
        static Image CreateProgressBar(RectTransform parent)
        {
            var slot = CreateImage("ProgressBar", parent, SliderBg);
            var le = slot.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 24f;
            le.preferredHeight = 24f;

            var fill = CreateImage("Fill", slot.transform, Accent);
            Stretch((RectTransform)fill.transform, Vector2.zero, Vector2.one);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            return fill;
        }

        // 用 SerializedObject 写私有序列化字段,避免反射(照搬 XRay 工厂做法)
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
            Set(so, "_reconstructAxisButton", r.ReconstructAxisButton);
            Set(so, "_reconstructAxisLabel", r.ReconstructAxisLabel);
            Set(so, "_rebuildButton", r.RebuildButton);
            Set(so, "_clipToggle", r.ClipToggle);
            Set(so, "_spawnClipButton", r.SpawnClipButton);
            Set(so, "_clearClipButton", r.ClearClipButton);
            Set(so, "_classColorToggle", r.ClassColorToggle);
            Set(so, "_lutColorToggle", r.LutColorToggle);
            Set(so, "_breakpointColorToggle", r.BreakpointColorToggle);
            Set(so, "_lutPresetButton", r.LutPresetButton);
            Set(so, "_lutPresetLabel", r.LutPresetLabel);
            Set(so, "_modelScaleSlider", r.ModelScale);
            Set(so, "_modelScaleLabel", r.ModelScaleLabel);
            Set(so, "_resetTransformButton", r.ResetTransformButton);
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
