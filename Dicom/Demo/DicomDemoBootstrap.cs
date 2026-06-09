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
            go.AddComponent<DicomGrabbableSetup>();
            go.AddComponent<TwoHandScaler>();
            var windowLevel = go.AddComponent<WindowLevelController>();
            var clipping = go.AddComponent<ClippingPlaneController>();

            _controller.OnProgress += r => Debug.Log($"DICOM 加载进度: {r * 100f:F0}%");
            _controller.OnError += e => Debug.LogError($"DICOM 加载错误: {e.Message}");

            // 调试面板挂在 bootstrap 物体上，绑定动态创建的各控制器
            if (_attachDebugPanel)
            {
                var panel = gameObject.AddComponent<DicomDebugPanel>();
                BindPanel(panel, pc, windowLevel, clipping);
            }

            _controller.Load(directory);
        }

        // 通过反射把动态创建的组件绑定到面板私有字段(Demo 便利)
        void BindPanel(DicomDebugPanel panel, DicomPointCloud pc, WindowLevelController windowLevel, ClippingPlaneController clipping)
        {
            SetPrivateField(panel, "_controller", _controller);
            SetPrivateField(panel, "_pointCloud", pc);
            SetPrivateField(panel, "_windowLevel", windowLevel);
            SetPrivateField(panel, "_clipping", clipping);
        }

        void SetPrivateField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null) field.SetValue(target, value);
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
