using UnityEngine;

using Dicom.PointCloud;

namespace Dicom.Interaction
{
    // 运行时调节显示：窗宽窗位走 shader 全局变量(无重算)，阈值变化触发点云重建
    public class WindowLevelController : MonoBehaviour
    {
        [SerializeField] PointCloudController _controller;
        [SerializeField, Range(0f, 1f)] float _windowCenter = 0.5f;
        [SerializeField, Range(0.01f, 1f)] float _windowWidth = 1f;

        [SerializeField] float _thresholdMin = 200f;
        [SerializeField] float _thresholdMax = 3000f;

        static readonly int _WindowId = Shader.PropertyToID("_DicomWindow");

        void OnEnable() => ApplyWindow();

        // 窗宽窗位作用于 0..1 归一化强度，shader 端做映射，零 CPU 重算
        public void SetWindow(float center, float width)
        {
            _windowCenter = Mathf.Clamp01(center);
            _windowWidth = Mathf.Clamp(width, 0.01f, 1f);
            ApplyWindow();
        }

        void ApplyWindow() => Shader.SetGlobalVector(_WindowId, new Vector4(_windowCenter, _windowWidth, 0f, 0f));

        // 阈值改变需要重新过滤体素，开销大，仅在用户确认调节后调用
        public void SetThreshold(float min, float max)
        {
            _thresholdMin = min;
            _thresholdMax = max;
            if (_controller != null) _controller.SetThreshold(min, max);
        }

        public float ThresholdMin => _thresholdMin;
        public float ThresholdMax => _thresholdMax;
    }
}
