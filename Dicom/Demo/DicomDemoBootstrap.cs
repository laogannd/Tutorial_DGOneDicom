using System.IO;
using UnityEngine;

using Dicom.Core;
using Dicom.PointCloud;
using Dicom.Interaction;

namespace Dicom.Demo
{
    // 示例引导：指定路径加载 DICOM 序列并挂上交互组件，便于 Play Mode 验证
    public class DicomDemoBootstrap : MonoBehaviour
    {
        [SerializeField] string _relativeDir = "dicom";
        [SerializeField] Material _pointMaterial;
        [SerializeField] DicomClassificationProfile _classificationProfile;
        [SerializeField] DicomLutProfile _lutProfile;
        [SerializeField] DicomBreakpointProfile _breakpointProfile;
        // 点云碰撞体预设忽略的层,统一传给运行时挂载的 DicomGrabbableSetup
        [SerializeField] LayerMask _excludeLayers;
        [SerializeField] bool _autoLoadOnStart = true;
        [SerializeField] bool _attachDebugPanel = true;

        PointCloudController _controller;

        void Start()
        {
            if (_autoLoadOnStart) LoadDefault();
        }

        // 从 persistentDataPath/<_relativeDir> 加载，便于 Pico 上 adb push 测试数据
        public void LoadDefault()
        {
            string dir = Path.Combine(Application.persistentDataPath, _relativeDir);
            Load(dir);
        }

        public void Load(string directory)
        {
            var go = new GameObject("DicomPointCloud");
            go.transform.SetParent(transform, false);

            var pc = go.AddComponent<DicomPointCloud>();
            SetMaterial(pc);

            _controller = go.AddComponent<PointCloudController>();
            if (_classificationProfile != null) _controller.SetClassificationProfile(_classificationProfile);
            if (_lutProfile != null) _controller.SetLutProfile(_lutProfile);
            if (_breakpointProfile != null) _controller.SetBreakpointProfile(_breakpointProfile);
            // 先挂 DicomGrabbableSetup，其 Awake 会建好刚体/碰撞体/Grabbable，
            // 再挂 TwoHandScaler，避免它的 RequireComponent 抢先创建未就绪的 Grabbable
            var grabbableSetup = go.AddComponent<DicomGrabbableSetup>();
            // Awake 已建好碰撞体,此处赋值立即把预设排除层应用上去
            grabbableSetup.ExcludeLayers = _excludeLayers;
            // 在 GrabbableSetup 之后挂,确保碰撞盒 BoxCollider 已就绪供线框读取尺寸
            go.AddComponent<DicomBoundingBoxVisualizer>();
            // DicomModelTransform 在缩放器之前挂,TwoHandScaler.Awake 才能取到它做相对缩放基准
            go.AddComponent<DicomModelTransform>();
            go.AddComponent<TwoHandScaler>();
            // 远程射线操控:自治组件,Awake 自动发现场景内的 DicomRayPointer,无需手动装配
            go.AddComponent<DicomRayManipulator>();
            var windowLevel = go.AddComponent<WindowLevelController>();
            var clipping = go.AddComponent<ClippingPlaneController>();

            _controller.OnProgress += r => Debug.Log($"DICOM 加载进度: {r * 100f:F0}%");
            _controller.OnError += e => Debug.LogError($"DICOM 加载错误: {e.Message}");

            // 接入统一面板:优先绑定场景已有面板,否则新建一个,再把 DICOM 组件绑上去
            if (_attachDebugPanel)
            {
                var modelTransform = go.GetComponent<DicomModelTransform>();
                var panel = FindObjectOfType<UnifiedDebugPanel>();
                if (panel == null) panel = gameObject.AddComponent<UnifiedDebugPanel>();
                panel.BindDicom(_controller, pc, windowLevel, clipping, modelTransform);
            }

            _controller.Load(directory);
        }

        // 通过反射设置序列化的私有 material 字段(Demo 便利，正式用 Inspector 赋值)
        void SetMaterial(DicomPointCloud pc)
        {
            if (_pointMaterial == null) return;
            var field = typeof(DicomPointCloud).GetField("_material",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null) field.SetValue(pc, _pointMaterial);
        }
    }
}
