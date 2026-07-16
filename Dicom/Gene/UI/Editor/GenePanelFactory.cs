using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Dicom.Gene;

namespace Dicom.UI.EditorTools
{
    // 一键生成世界空间基因操作面板,复用 DicomPanelFactory 的全部控件 helper(partial 同类)
    // 菜单 GameObject/Dicom/创建基因 VR 面板;结构对齐 DICOM 面板但分区换成基因功能
    public static partial class DicomPanelFactory
    {
        // top 基因按钮固定槽数(与 GenePanelUI 数组对应)
        const int GeneTopSlots = 5;

        [MenuItem("GameObject/Dicom/创建基因 VR 面板", false, 12)]
        public static void CreateGenePanelInScene()
        {
            var panel = BuildGenePanel();
            Selection.activeGameObject = panel;
            Undo.RegisterCreatedObjectUndo(panel, "创建基因 VR 面板");
            EditorGUIUtility.PingObject(panel);
        }

        [MenuItem("GameObject/Dicom/创建基因 VR 面板并存为预制体", false, 13)]
        public static void CreateGenePanelPrefab()
        {
            var panel = BuildGenePanel();
            EnsureDir(PrefabDir);
            string path = AssetDatabase.GenerateUniqueAssetPath(PrefabDir + "/GenePanel.prefab");
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(panel, path, InteractionMode.UserAction);
            Selection.activeGameObject = panel;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log($"基因 VR 面板预制体已保存: {path}");
        }

        // 搭建完整基因面板层级(复用 DICOM 面板的 Canvas/滚动区/标题条/抓取手柄/加厚碰撞体)
        static GameObject BuildGenePanel()
        {
            var root = new GameObject("GenePanel");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            root.AddComponent<GraphicRaycaster>();

            var rootRt = (RectTransform)root.transform;
            rootRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rootRt.localScale = Vector3.one * 0.001f;

            root.AddComponent<Image>().color = PanelBg;

            CreateGeneTitleBar(rootRt);
            var content = CreateScrollView(rootRt, out _);

            var panelUI = root.AddComponent<GenePanelUI>();
            var refs = BuildGeneSections(content);
            BindGenePanel(panelUI, refs);

            ConfigureGrabHandle(root);
            ThickenPokeColliders(root);

            return root;
        }

        // 基因面板标题条(复用 DICOM 标题条样式,改文案)
        static void CreateGeneTitleBar(RectTransform panel)
        {
            var bar = CreateImage("TitleBar", panel, HeaderBg);
            bar.gameObject.AddComponent<DicomPanelRayDragMover>();
            var barRt = (RectTransform)bar.transform;
            barRt.anchorMin = new Vector2(0f, 1f);
            barRt.anchorMax = new Vector2(1f, 1f);
            barRt.pivot = new Vector2(0.5f, 1f);
            barRt.sizeDelta = new Vector2(0f, TitleBarHeight);
            barRt.anchoredPosition = Vector2.zero;

            var label = CreateLabel("Title", bar.transform, "基因表达面板", 30f, TitleBarHeight);
            label.alignment = TextAlignmentOptions.Center;
            label.fontStyle = FontStyles.Bold;
            label.color = Accent;
            Stretch((RectTransform)label.transform, Vector2.zero, Vector2.one);

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

        // 基因面板控件引用收集
        class GenePanelRefs
        {
            public TextMeshProUGUI StatusText;
            public Image ProgressFill;
            public Toggle RegionModeToggle;
            public Button PrevGene, NextGene;
            public TextMeshProUGUI GeneLabel;
            public TextMeshProUGUI SearchKeywordLabel;
            public Button[] KeyButtons;
            public Button BackspaceButton, ClearKeywordButton;
            public RectTransform SearchResultContent;
            public Button SearchResultTemplate;
            public TextMeshProUGUI SearchResultCountLabel;
            public Button LutPresetButton;
            public TextMeshProUGUI LutPresetLabel;
            public Toggle BrushToggle;
            public Button ClearButton, AnalyzeButton;
            public TextMeshProUGUI SelectionLabel;
            public Slider BrushRadius;
            public TextMeshProUGUI BrushRadiusLabel;
            public TextMeshProUGUI RegionNameLabel;
            public Button[] TopGeneButtons = new Button[GeneTopSlots];
            public TextMeshProUGUI[] TopGeneLabels = new TextMeshProUGUI[GeneTopSlots];
            public Slider ModelScale;
            public TextMeshProUGUI ModelScaleLabel;
            public Button ResetTransformButton;
        }

        static GenePanelRefs BuildGeneSections(RectTransform content)
        {
            var r = new GenePanelRefs();

            var status = CreateSection(content, "加载状态", true);
            r.StatusText = CreateLabel("StatusText", status, "阶段: 空闲", 20f, 150f);
            r.ProgressFill = CreateProgressBar(status);

            var mode = CreateSection(content, "模式", true);
            r.RegionModeToggle = CreateToggle("RegionModeToggle", mode, "区域模式 (mode2)");

            var gene = CreateSection(content, "基因选择", true);
            r.GeneLabel = CreateLabel("GeneLabel", gene, "基因: (未选)", 22f, LabelHeight);
            r.PrevGene = CreateButton("PrevGeneButton", gene, "上一个基因");
            r.NextGene = CreateButton("NextGeneButton", gene, "下一个基因");

            // 基因搜索:虚拟键盘输入关键字 -> 实时筛选 -> 可滚动结果按钮点选(上万基因时替代循环翻页)
            var search = CreateSection(content, "基因搜索", true);
            r.SearchKeywordLabel = CreateLabel("SearchKeywordLabel", search, "关键字: (空)", 22f, LabelHeight);
            r.KeyButtons = CreateKeyboard(search, out r.BackspaceButton, out r.ClearKeywordButton);
            r.SearchResultCountLabel = CreateLabel("SearchResultCountLabel", search, "", 20f, LabelHeight);
            r.SearchResultContent = CreateResultList(search, out r.SearchResultTemplate);

            var colormap = CreateSection(content, "Colormap", true);
            r.LutPresetLabel = CreateLabel("LutPresetLabel", colormap, "Colormap: HotIron", 20f, LabelHeight);
            r.LutPresetButton = CreateButton("LutPresetButton", colormap, "切换 Colormap");

            var brush = CreateSection(content, "空间画笔 (mode2)", true);
            r.BrushToggle = CreateToggle("BrushToggle", brush, "启用画笔(球形)");
            r.BrushRadiusLabel = CreateLabel("BrushRadiusLabel", brush, "笔刷半径: 3.0 cm", 20f, LabelHeight);
            r.BrushRadius = CreateSlider("BrushRadiusSlider", brush);
            r.SelectionLabel = CreateLabel("SelectionLabel", brush, "已选 cell: 0", 20f, LabelHeight);
            r.ClearButton = CreateButton("ClearButton", brush, "清除选择");
            r.AnalyzeButton = CreateButton("AnalyzeButton", brush, "确认分析");

            var region = CreateSection(content, "区域结果", true);
            r.RegionNameLabel = CreateLabel("RegionNameLabel", region, "区域: (未分析)", 22f, LabelHeight);
            for (int i = 0; i < GeneTopSlots; i++)
            {
                var btn = CreateButton($"TopGene{i}", region, $"{i + 1}. ---");
                r.TopGeneButtons[i] = btn;
                // 按钮内标签(CreateButton 建了名为 Text 的居中标签)
                r.TopGeneLabels[i] = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            var transform = CreateSection(content, "模型变换", true);
            r.ModelScaleLabel = CreateLabel("ModelScaleLabel", transform, "模型缩放", 20f, LabelHeight);
            r.ModelScale = CreateSlider("ModelScaleSlider", transform);
            r.ResetTransformButton = CreateButton("ResetTransformButton", transform, "复位位置/大小");

            return r;
        }

        // 虚拟键盘:A-Z + 0-9 网格键 + 退格/清空;每键复用 CreateButton(自带 UIPokeBridge 手指戳)
        // 网格用 GridLayoutGroup 固定 cellSize 排布,忽略按钮内 LayoutElement 尺寸无害
        static Button[] CreateKeyboard(RectTransform parent, out Button backspace, out Button clearKw)
        {
            // 键盘网格容器:7 列,cell 按面板内容宽自适应估算
            var gridRt = CreateRect("Keyboard", parent);
            var grid = gridRt.gameObject.AddComponent<GridLayoutGroup>();
            const int cols = 7;
            // 面板内容区约 PanelWidth-滚动条-卡片内边距,取 74px 方格 + 6px 间距,7 列约 554px
            grid.cellSize = new Vector2(74f, 62f);
            grid.spacing = new Vector2(6f, 6f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = cols;
            grid.childAlignment = TextAnchor.UpperCenter;
            gridRt.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string digits = "0123456789";
            var keys = new Button[letters.Length + digits.Length];
            int k = 0;
            foreach (char c in letters) keys[k++] = CreateKeyButton(gridRt, c);
            foreach (char c in digits) keys[k++] = CreateKeyButton(gridRt, c);

            // 退格/清空单独两键,跟在网格末尾
            backspace = CreateButton("KeyBackspace", gridRt, "退格");
            clearKw = CreateButton("KeyClear", gridRt, "清空");
            return keys;
        }

        // 单个字符键:标签即字符,GenePanelUI 运行时读子标签文字绑定输入
        static Button CreateKeyButton(RectTransform parent, char c)
        {
            return CreateButton("Key_" + c, parent, c.ToString());
        }

        // 结果列表:VerticalLayoutGroup 容器(进主滚动区,靠主滚动条滚动) + 结果项模板按钮
        static RectTransform CreateResultList(RectTransform parent, out Button template)
        {
            var listRt = CreateRect("SearchResultList", parent);
            var layout = listRt.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            listRt.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 结果项模板:初始隐藏不占布局,运行时 Instantiate 复用,副本沿用厚碰撞体
            template = CreateButton("SearchResultTemplate", listRt, "---");
            template.gameObject.SetActive(false);
            return listRt;
        }

        static void BindGenePanel(GenePanelUI panel, GenePanelRefs r)
        {
            var so = new SerializedObject(panel);
            Set(so, "_statusText", r.StatusText);
            Set(so, "_progressFill", r.ProgressFill);
            Set(so, "_regionModeToggle", r.RegionModeToggle);
            Set(so, "_prevGeneButton", r.PrevGene);
            Set(so, "_nextGeneButton", r.NextGene);
            Set(so, "_geneLabel", r.GeneLabel);
            Set(so, "_searchKeywordLabel", r.SearchKeywordLabel);
            SetArray(so, "_keyButtons", r.KeyButtons);
            Set(so, "_backspaceButton", r.BackspaceButton);
            Set(so, "_clearKeywordButton", r.ClearKeywordButton);
            Set(so, "_searchResultContent", r.SearchResultContent);
            Set(so, "_searchResultTemplate", r.SearchResultTemplate);
            Set(so, "_searchResultCountLabel", r.SearchResultCountLabel);
            Set(so, "_lutPresetButton", r.LutPresetButton);
            Set(so, "_lutPresetLabel", r.LutPresetLabel);
            Set(so, "_brushToggle", r.BrushToggle);
            Set(so, "_clearButton", r.ClearButton);
            Set(so, "_analyzeButton", r.AnalyzeButton);
            Set(so, "_selectionLabel", r.SelectionLabel);
            Set(so, "_brushRadiusSlider", r.BrushRadius);
            Set(so, "_brushRadiusLabel", r.BrushRadiusLabel);
            Set(so, "_regionNameLabel", r.RegionNameLabel);
            SetArray(so, "_topGeneButtons", r.TopGeneButtons);
            SetArray(so, "_topGeneLabels", r.TopGeneLabels);
            Set(so, "_modelScaleSlider", r.ModelScale);
            Set(so, "_modelScaleLabel", r.ModelScaleLabel);
            Set(so, "_resetTransformButton", r.ResetTransformButton);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // 写数组序列化字段(top 基因按钮/标签槽)
        static void SetArray(SerializedObject so, string field, Object[] values)
        {
            var prop = so.FindProperty(field);
            if (prop == null) return;
            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }
}
