using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

using Dicom.UI;

namespace Dicom.UI.EditorTools
{
    // 一键生成世界空间 DICOM 操作面板:Canvas + 滚动区 + 折叠分区控件 + 抓取手柄
    // 菜单 GameObject/Dicom/创建 VR 操作面板:在场景生成可直接调整
    // 菜单 GameObject/Dicom/创建 VR 操作面板并存为预制体:生成后另存到 Prefabs
    public static partial class DicomPanelFactory
    {
        const string PrefabDir = "Assets/!!Workspace/_Workspace/Script/Dicom/Prefabs";
        // 加大控件后面板加宽到 640,高 1000,世界空间 0.001 缩放 -> 约 0.64m x 1.0m
        const float PanelWidth = 640f;
        const float PanelHeight = 1000f;
        // 固定顶部标题条高度,兼作抓取手柄区域;脱离滚动区始终可见
        const float TitleBarHeight = 90f;
        // 粗垂直滚动条宽度,便于 VR 手指推与射线拖
        const float ScrollbarWidth = 44f;

        [MenuItem("GameObject/Dicom/创建 VR 操作面板", false, 10)]
        public static void CreateInScene()
        {
            var panel = BuildPanel();
            Selection.activeGameObject = panel;
            Undo.RegisterCreatedObjectUndo(panel, "创建 Dicom VR 操作面板");
            EditorGUIUtility.PingObject(panel);
        }

        [MenuItem("GameObject/Dicom/创建 VR 操作面板并存为预制体", false, 11)]
        public static void CreateAndSavePrefab()
        {
            var panel = BuildPanel();
            EnsureDir(PrefabDir);
            string path = AssetDatabase.GenerateUniqueAssetPath(PrefabDir + "/DicomPanel.prefab");
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(panel, path, InteractionMode.UserAction);
            Selection.activeGameObject = panel;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log($"Dicom VR 操作面板预制体已保存: {path}");
        }

        // 搭建完整面板层级,返回根物体
        static GameObject BuildPanel()
        {
            var root = new GameObject("DicomPanel");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            root.AddComponent<GraphicRaycaster>();

            var rootRt = (RectTransform)root.transform;
            rootRt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            // 世界空间 0.001 缩放 -> 面板物理尺寸约 0.64m x 1.0m,舒适手够范围
            rootRt.localScale = Vector3.one * 0.001f;

            // 背景
            var bg = root.AddComponent<Image>();
            bg.color = PanelBg;

            // 固定顶部标题条:脱离滚动区,始终可见,兼作抓取手柄视觉提示
            CreateTitleBar(rootRt);

            // 滚动视图(控件多需滚动):让出顶部标题条高度,右侧留粗滚动条
            var content = CreateScrollView(rootRt, out _);

            var panelUI = root.AddComponent<DicomPanelUI>();
            var refs = BuildSections(content);
            BindPanel(panelUI, refs);

            // 抓取手柄:挂 Canvas 根,碰撞盒精确覆盖顶部固定标题条,可双手抓拖面板
            ConfigureGrabHandle(root);

            // 加厚手指触碰碰撞体:防 VR 手指快戳穿透漏判
            ThickenPokeColliders(root);

            return root;
        }

        // 抓取碰撞盒对齐顶部固定标题条:中心在面板顶端下移半个标题条高,尺寸贴合标题条
        static DicomPanelGrabHandle ConfigureGrabHandle(GameObject root)
        {
            var grab = root.AddComponent<DicomPanelGrabHandle>();
            grab.Configure(
                new Vector3(PanelWidth, TitleBarHeight, 20f),
                new Vector3(0f, PanelHeight * 0.5f - TitleBarHeight * 0.5f, 0f));
            return grab;
        }

        // 把所有 PokeSlider / UIPokeBridge 的碰撞体深度调厚,中心前移加大
        // 序列化字段在运行时 Awake 才生效,这里编辑器期写好值,生成的预制体即带厚碰撞体
        static void ThickenPokeColliders(GameObject root)
        {
            foreach (var poke in root.GetComponentsInChildren<PokeSlider>(true))
            {
                var so = new SerializedObject(poke);
                SetFloat(so, "_colliderDepth", 0.04f);
                SetFloat(so, "_colliderForwardOffset", 0.012f);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var poke in root.GetComponentsInChildren<VRQuestion.UIPokeBridge>(true))
            {
                var so = new SerializedObject(poke);
                SetFloat(so, "_colliderDepth", 0.03f);
                SetFloat(so, "_colliderForwardOffset", 0.01f);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var poke in root.GetComponentsInChildren<PokeScrollbar>(true))
            {
                var so = new SerializedObject(poke);
                SetFloat(so, "_colliderDepth", 0.04f);
                SetFloat(so, "_colliderForwardOffset", 0.012f);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void SetFloat(SerializedObject so, string field, float value)
        {
            var prop = so.FindProperty(field);
            if (prop != null) prop.floatValue = value;
        }

        // 创建 ScrollRect + Viewport + Content(竖直布局) + 粗垂直滚动条,返回 content 容器
        // topInset:标题条以下额外让出的高度(统一面板用于让出标签栏)
        static RectTransform CreateScrollView(RectTransform parent, out ScrollRect scrollRect, float topInset = 0f)
        {
            var viewport = CreateRect("Viewport", parent);
            Stretch(viewport, Vector2.zero, Vector2.one);
            // 顶部让出固定标题条(+额外 inset),右侧让出粗滚动条
            viewport.offsetMin = new Vector2(12f, 12f);
            viewport.offsetMax = new Vector2(-(ScrollbarWidth + 16f), -(TitleBarHeight + 8f + topInset));
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);

            scrollRect = parent.gameObject.AddComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 20f;

            var content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(0f, 0f);

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = content;

            // 粗垂直滚动条:贴面板右侧,顶部对齐滚动区,手指可推
            var scrollbar = CreateVerticalScrollbar(parent, topInset);
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            return content;
        }

        // 创建粗垂直滚动条:贴面板右侧,加宽 handle 便于 VR 手指推;PokeSlider 复用滑块投影逻辑不适用,这里靠射线拖 + 滚动联动
        static Scrollbar CreateVerticalScrollbar(RectTransform parent, float topInset = 0f)
        {
            var rt = CreateRect("VerticalScrollbar", parent);
            // 锚到右侧,顶部对齐滚动区底部高度,纵向铺满滚动区
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            rt.offsetMin = new Vector2(-(ScrollbarWidth + 8f), 12f);
            rt.offsetMax = new Vector2(-8f, -(TitleBarHeight + 8f + topInset));

            var bgImg = rt.gameObject.AddComponent<Image>();
            bgImg.color = SliderBg;

            var scrollbar = rt.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            // 滑动区域 + 把手:把手加宽填满,圆润青色,易戳中
            var slidingArea = CreateRect("Sliding Area", rt);
            Stretch(slidingArea, Vector2.zero, Vector2.one);
            slidingArea.offsetMin = new Vector2(4f, 4f);
            slidingArea.offsetMax = new Vector2(-4f, -4f);

            var handle = CreateImage("Handle", slidingArea, Accent);
            var handleRt = (RectTransform)handle.transform;
            Stretch(handleRt, Vector2.zero, Vector2.one);
            handleRt.offsetMin = new Vector2(2f, 2f);
            handleRt.offsetMax = new Vector2(-2f, -2f);

            scrollbar.targetGraphic = handle;
            scrollbar.handleRect = handleRt;
            scrollbar.transition = Selectable.Transition.ColorTint;
            scrollbar.colors = MakeColorBlock(Accent, ButtonPressed, ButtonPressed);

            // 手指可推:复用 PokeSlider 的轨道投影驱动滚动条 normalizedValue
            rt.gameObject.AddComponent<PokeScrollbar>();
            return scrollbar;
        }
    }
}
