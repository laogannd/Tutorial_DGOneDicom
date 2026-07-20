using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

using Dicom.UI;
using Dicom.Gene;

namespace Dicom.UI.EditorTools
{
    // 一键生成"超级统一面板":单面板顶部标签页切换 DICOM / 基因表达 两大控制模块
    // 软跟随 HUD(VRHudFollower)绑定头显相机,惰性跟随视野;仍可抓取/射线拖动微调
    // 复用 DicomPanelFactory 的全部控件 helper 与 DicomPanelUI/GenePanelUI 运行时逻辑
    public static partial class DicomPanelFactory
    {
        // 标签栏高度:固定在标题条之下,不进滚动区
        const float TabBarHeight = 78f;

        [MenuItem("GameObject/Dicom/创建统一超级面板 (绑定相机)", false, 8)]
        public static void CreateUnifiedPanelInScene()
        {
            var panel = BuildUnifiedPanel();
            Selection.activeGameObject = panel;
            Undo.RegisterCreatedObjectUndo(panel, "创建统一超级面板");
            EditorGUIUtility.PingObject(panel);
        }

        [MenuItem("GameObject/Dicom/创建统一超级面板并存为预制体", false, 9)]
        public static void CreateUnifiedPanelPrefab()
        {
            var panel = BuildUnifiedPanel();
            EnsureDir(PrefabDir);
            string path = AssetDatabase.GenerateUniqueAssetPath(PrefabDir + "/UnifiedPanel.prefab");
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(panel, path, InteractionMode.UserAction);
            Selection.activeGameObject = panel;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log($"统一超级面板预制体已保存: {path}");
        }

        // 搭建统一面板层级:Canvas + 标题条 + 标签栏 + 滚动区(内含 DICOM/基因两页)
        static GameObject BuildUnifiedPanel()
        {
            var root = new GameObject("UnifiedPanel");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            root.AddComponent<GraphicRaycaster>();

            var rootRt = (RectTransform)root.transform;
            rootRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rootRt.localScale = Vector3.one * 0.001f;
            root.AddComponent<Image>().color = PanelBg;

            CreateUnifiedTitleBar(rootRt);
            // 标签栏固定在标题条之下,滚动区顶部让出标签栏高度
            var tabRefs = CreateTabBar(rootRt);
            var content = CreateScrollView(rootRt, out _, TabBarHeight);

            // 两页容器进滚动 content,各自竖直布局;标签切换互斥显示
            var dicomPage = CreatePageContainer(content, "DicomPage");
            var genePage = CreatePageContainer(content, "GenePage");

            // DICOM 模块:控件建进 dicomPage,DicomPanelUI 挂根,运行时自动绑数据源
            var dicomRefs = BuildSections(dicomPage);
            var dicomUI = root.AddComponent<DicomPanelUI>();
            BindPanel(dicomUI, dicomRefs);

            // 基因模块:控件建进 genePage,GenePanelUI 挂根,运行时自动绑数据源
            var geneRefs = BuildGeneSections(genePage);
            var geneUI = root.AddComponent<GenePanelUI>();
            BindGenePanel(geneUI, geneRefs);

            // 标签页切换组件:回填两页与两标签按钮
            var tabs = root.AddComponent<UnifiedPanelTabs>();
            tabs.Bind(tabRefs.DicomTab, tabRefs.GeneTab, dicomPage.gameObject, genePage.gameObject);

            // 软跟随 HUD + 抓取手柄 + 加厚碰撞体
            var follower = root.AddComponent<VRHudFollower>();
            var mainCam = Camera.main;
            if (mainCam != null) follower.SetCamera(mainCam);
            ConfigureGrabHandle(root);
            ThickenPokeColliders(root);

            return root;
        }

        // 统一面板标题条(复用样式,改文案);兼作射线拖拽区与抓取手柄视觉提示
        static void CreateUnifiedTitleBar(RectTransform panel)
        {
            var bar = CreateImage("TitleBar", panel, HeaderBg);
            bar.gameObject.AddComponent<DicomPanelRayDragMover>();
            var barRt = (RectTransform)bar.transform;
            barRt.anchorMin = new Vector2(0f, 1f);
            barRt.anchorMax = new Vector2(1f, 1f);
            barRt.pivot = new Vector2(0.5f, 1f);
            barRt.sizeDelta = new Vector2(0f, TitleBarHeight);
            barRt.anchoredPosition = Vector2.zero;

            var label = CreateLabel("Title", bar.transform, "统一控制台", 30f, TitleBarHeight);
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

        class TabRefs { public Button DicomTab; public Button GeneTab; }

        // 标签栏:标题条之下固定横排两个大标签按钮,不进滚动区
        static TabRefs CreateTabBar(RectTransform panel)
        {
            var barRt = CreateRect("TabBar", panel);
            barRt.anchorMin = new Vector2(0f, 1f);
            barRt.anchorMax = new Vector2(1f, 1f);
            barRt.pivot = new Vector2(0.5f, 1f);
            barRt.sizeDelta = new Vector2(0f, TabBarHeight);
            // 紧贴标题条下方
            barRt.anchoredPosition = new Vector2(0f, -TitleBarHeight);

            var layout = barRt.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(12, 12, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            var r = new TabRefs();
            r.DicomTab = CreateTabButton(barRt, "DICOM");
            r.GeneTab = CreateTabButton(barRt, "基因表达");
            return r;
        }

        // 标签按钮:复用 CreateButton(自带 UIPokeBridge 手指戳 + hover 反馈),横排由布局撑高
        static Button CreateTabButton(RectTransform parent, string text)
        {
            return CreateButton("Tab_" + text, parent, text);
        }

        // 页容器:进滚动 content 的竖直布局子物体,承载一个模块的全部折叠分区
        // 各页独立 ContentSizeFitter,切换隐藏另一页时布局按当前页高度收缩
        static RectTransform CreatePageContainer(RectTransform content, string name)
        {
            var page = CreateRect(name, content);
            var layout = page.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            page.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return page;
        }
    }
}
