using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Dicom.UI.EditorTools
{
    // 一键生成世界空间 DICOM 操作面板：Canvas + 滚动区 + 全部控件 + UIPokeBridge 绑定
    // 菜单 GameObject/Dicom/创建 VR 操作面板：在场景生成可直接调整
    // 菜单 GameObject/Dicom/创建 VR 操作面板并存为预制体：生成后另存到 Prefabs
    public static partial class DicomPanelFactory
    {
        const string PrefabDir = "Assets/!!Workspace/_Workspace/Script/Dicom/Prefabs";
        const float PanelWidth = 520f;
        const float PanelHeight = 900f;

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

        // 搭建完整面板层级，返回根物体
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
            // 世界空间 0.001 缩放 -> 面板物理尺寸约 0.52m x 0.9m，舒适手够范围
            rootRt.localScale = Vector3.one * 0.001f;

            // 背景
            var bg = root.AddComponent<Image>();
            bg.color = PanelBg;

            // 滚动视图(控件多需滚动)
            var content = CreateScrollView(rootRt, out _);

            var panelUI = root.AddComponent<DicomPanelUI>();
            var refs = BuildSections(content);
            BindPanel(panelUI, refs);

            return root;
        }

        // 创建 ScrollRect + Viewport + Content(竖直布局)，返回 content 容器
        static RectTransform CreateScrollView(RectTransform parent, out ScrollRect scrollRect)
        {
            var viewport = CreateRect("Viewport", parent);
            Stretch(viewport, Vector2.zero, Vector2.one);
            viewport.offsetMin = new Vector2(12f, 12f);
            viewport.offsetMax = new Vector2(-12f, -12f);
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
            layout.spacing = 8f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = content;
            return content;
        }
    }
}
