using UnityEditor;
using UnityEngine;

using Dicom.PointCloud;

namespace Dicom.Interaction.EditorTools
{
    // 一键创建可抓取裁切平面手柄,绑定到 ClippingPlaneController
    // 菜单 GameObject/Dicom/为点云创建裁切平面:在选中点云下生成手柄并自动绑定
    // 菜单 GameObject/Dicom/创建裁切平面(独立):不依赖点云,空场景也能先放好裁切平面
    public static class ClippingPlaneFactory
    {
        const float DefaultExtent = 0.3f;

        [MenuItem("GameObject/Dicom/为点云创建裁切平面", false, 12)]
        public static void CreateForSelected()
        {
            var target = Selection.activeGameObject;
            if (target == null || target.GetComponent<PointCloudController>() == null)
            {
                EditorUtility.DisplayDialog("裁切平面", "请先在层级中选中带 PointCloudController 的点云物体", "好");
                return;
            }

            var controller = target.GetComponent<ClippingPlaneController>();
            if (controller == null)
                controller = Undo.AddComponent<ClippingPlaneController>(target);

            float extent = ResolveExtent(target.transform);
            // 手柄建为场景根物体(parent=null),不挂点云下:与运行时一致,避免嵌套刚体导致抓点云异常
            var handle = ClipPlaneHandleBuilder.Build(null, extent);
            Undo.RegisterCreatedObjectUndo(handle, "创建裁切平面手柄");

            BindHandle(controller, handle.transform);

            Selection.activeGameObject = handle;
            EditorGUIUtility.PingObject(handle);
        }

        // 独立创建:不要求选中点云,新建带 ClippingPlaneController 的物体并生成手柄
        [MenuItem("GameObject/Dicom/创建裁切平面(独立)", false, 13)]
        public static void CreateStandalone()
        {
            var root = new GameObject("DicomClipPlane");
            Undo.RegisterCreatedObjectUndo(root, "创建独立裁切平面");

            // 放到场景视图焦点处,方便立即看到
            var view = SceneView.lastActiveSceneView;
            if (view != null) root.transform.position = view.pivot;

            var controller = Undo.AddComponent<ClippingPlaneController>(root);
            var handle = ClipPlaneHandleBuilder.Build(root.transform, DefaultExtent);
            Undo.RegisterCreatedObjectUndo(handle, "创建裁切平面手柄");

            BindHandle(controller, handle.transform);

            Selection.activeGameObject = handle;
            EditorGUIUtility.PingObject(handle);
        }

        // 用 SerializedObject 回填私有 _planeHandle,符合编辑器序列化规范并支持 Undo
        static void BindHandle(ClippingPlaneController controller, Transform handle)
        {
            var so = new SerializedObject(controller);
            so.FindProperty("_planeHandle").objectReferenceValue = handle;
            so.FindProperty("_enabled").boolValue = true;
            so.ApplyModifiedProperties();
        }

        // 优先按数据集体积取尺寸(Play 模式已加载),否则用默认值
        static float ResolveExtent(Transform parent)
        {
            var controller = parent.GetComponent<PointCloudController>();
            var dataset = controller != null ? controller.Dataset : null;
            if (dataset == null) return DefaultExtent;

            float x = dataset.Width * dataset.Spacing.x;
            float z = dataset.Depth * dataset.Spacing.z;
            return Mathf.Max(x, z);
        }
    }
}
