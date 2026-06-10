using UnityEditor;
using UnityEngine;
using Autohand;

using Dicom.PointCloud;

namespace Dicom.Interaction.EditorTools
{
    // 一键为选中的点云生成可抓取裁切平面手柄，并绑定到 ClippingPlaneController
    // 菜单 GameObject/Dicom/为点云创建裁切平面：在选中点云下生成手柄并自动绑定
    public static class ClippingPlaneFactory
    {
        const float DefaultExtent = 0.3f;
        const float HandleThickness = 0.01f;

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

            var handle = BuildHandle(target.transform);
            Undo.RegisterCreatedObjectUndo(handle, "创建裁切平面手柄");

            // 用 SerializedObject 回填私有 _planeHandle，符合编辑器序列化规范并支持 Undo
            var so = new SerializedObject(controller);
            so.FindProperty("_planeHandle").objectReferenceValue = handle.transform;
            so.FindProperty("_enabled").boolValue = true;
            so.ApplyModifiedProperties();

            Selection.activeGameObject = handle;
            EditorGUIUtility.PingObject(handle);
        }

        // 手柄根挂刚体+碰撞体+Grabbable 供 VR 抓取，子物体 Quad 做可视薄片
        // 根的 up 方向即裁切法线，与 ClippingPlaneController.LateUpdate 取法一致
        static GameObject BuildHandle(Transform parent)
        {
            float extent = ResolveExtent(parent);

            var handle = new GameObject("ClipPlaneHandle");
            handle.transform.SetParent(parent, false);
            handle.transform.localPosition = Vector3.zero;
            handle.layer = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);

            var body = handle.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            var col = handle.AddComponent<BoxCollider>();
            col.size = new Vector3(extent, HandleThickness, extent);

            var grab = handle.AddComponent<Grabbable>();
            grab.body = body;
            grab.singleHandOnly = false;
            grab.instantGrab = false;

            BuildVisual(handle.transform, extent);
            return handle;
        }

        // 可视薄片：Quad 默认法线朝 +Z，绕 X 转 90 度后法线朝 +Y，与手柄 up 对齐
        static void BuildVisual(Transform handle, float extent)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Visual";
            // Quad 自带 MeshCollider，裁切片不需要碰撞，移除避免干扰抓取检测
            Object.DestroyImmediate(quad.GetComponent<MeshCollider>());
            quad.transform.SetParent(handle, false);
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            quad.transform.localScale = new Vector3(extent, extent, 1f);

            var renderer = quad.GetComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.2f, 0.7f, 1f, 1f) };
            renderer.sharedMaterial = mat;
        }

        // 优先按数据集体积取尺寸(Play 模式已加载)，否则用默认值
        static float ResolveExtent(Transform parent)
        {
            var controller = parent.GetComponent<PointCloudController>();
            var dataset = controller.Dataset;
            if (dataset == null) return DefaultExtent;

            float x = dataset.Width * dataset.Spacing.x;
            float z = dataset.Depth * dataset.Spacing.z;
            return Mathf.Max(x, z);
        }
    }
}
