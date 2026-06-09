using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Dicom.Core;
using Dicom.PointCloud;
using Dicom.Interaction;

namespace Dicom.UI
{
    // 世界空间 UGUI 版 DICOM 操作面板，功能对齐 DicomDebugPanel，供 VR 手交互
    // 控件引用由 DicomPanelFactory 编辑器工厂绑定；兼容 HandCanvasPointer 射线与 UIPokeBridge 手指触碰
    [AddComponentMenu("Dicom/Dicom Panel UI")]
    public class DicomPanelUI : MonoBehaviour
    {
        [Header("数据源")]
        [SerializeField] PointCloudController _controller;
        [SerializeField] DicomPointCloud _pointCloud;
        [SerializeField] WindowLevelController _windowLevel;
        [SerializeField] ClippingPlaneController _clipping;

        [Header("状态")]
        [SerializeField] TextMeshProUGUI _statusText;
        [SerializeField] Image _progressFill;

        [Header("外观-实时")]
        [SerializeField] Slider _pointSizeSlider;
        [SerializeField] TextMeshProUGUI _pointSizeLabel;
        [SerializeField] Slider _windowCenterSlider;
        [SerializeField] TextMeshProUGUI _windowCenterLabel;
        [SerializeField] Slider _windowWidthSlider;
        [SerializeField] TextMeshProUGUI _windowWidthLabel;
        [SerializeField] Slider _gainSlider;
        [SerializeField] TextMeshProUGUI _gainLabel;
        [SerializeField] Slider _tintRSlider;
        [SerializeField] Slider _tintGSlider;
        [SerializeField] Slider _tintBSlider;

        [Header("点生成-需Apply")]
        [SerializeField] Slider _thresholdMinSlider;
        [SerializeField] TextMeshProUGUI _thresholdMinLabel;
        [SerializeField] Slider _thresholdMaxSlider;
        [SerializeField] TextMeshProUGUI _thresholdMaxLabel;
        [SerializeField] Slider _normalizeMinSlider;
        [SerializeField] TextMeshProUGUI _normalizeMinLabel;
        [SerializeField] Slider _normalizeMaxSlider;
        [SerializeField] TextMeshProUGUI _normalizeMaxLabel;
        [SerializeField] Button _applyThresholdButton;
        [SerializeField] Button _applyNormalizeButton;

        [Header("开关")]
        [SerializeField] Toggle _clipToggle;
        [SerializeField] Toggle _classColorToggle;

        float _tintR = 1f, _tintG = 1f, _tintB = 1f, _gain = 1f;

        static readonly int _TintId = Shader.PropertyToID("_DicomTint");

        void Start()
        {
            // 未显式绑定时从子物体自动查找(与 DicomDebugPanel 一致)
            if (_controller == null) _controller = GetComponentInChildren<PointCloudController>();
            if (_pointCloud == null) _pointCloud = GetComponentInChildren<DicomPointCloud>();
            if (_windowLevel == null) _windowLevel = GetComponentInChildren<WindowLevelController>();
            if (_clipping == null) _clipping = GetComponentInChildren<ClippingPlaneController>();

            SetupControls();
            ApplyTint();
            if (_controller != null)
            {
                _controller.OnReportChanged += OnReportChanged;
                RefreshStatus(_controller.Report);
            }
        }

        void OnDestroy()
        {
            if (_controller != null) _controller.OnReportChanged -= OnReportChanged;
        }

        // 设定滑块范围与初值并挂回调，范围在此集中管理避免工厂硬编码
        void SetupControls()
        {
            if (_pointCloud != null)
                ConfigSlider(_pointSizeSlider, 0.0001f, 0.02f, 0.002f, OnPointSizeChanged);

            ConfigSlider(_windowCenterSlider, 0f, 1f, 0.5f, OnWindowChanged);
            ConfigSlider(_windowWidthSlider, 0.01f, 1f, 1f, OnWindowChanged);
            ConfigSlider(_gainSlider, 0.1f, 4f, 1f, OnTintChanged);
            ConfigSlider(_tintRSlider, 0f, 1f, 1f, OnTintChanged);
            ConfigSlider(_tintGSlider, 0f, 1f, 1f, OnTintChanged);
            ConfigSlider(_tintBSlider, 0f, 1f, 1f, OnTintChanged);

            if (_controller != null)
            {
                ConfigSlider(_thresholdMinSlider, -1000f, 3000f, _controller.ThresholdMin, OnThresholdLabelChanged);
                ConfigSlider(_thresholdMaxSlider, -1000f, 4000f, _controller.ThresholdMax, OnThresholdLabelChanged);
                ConfigSlider(_normalizeMinSlider, -1000f, 3000f, _controller.NormalizeMin, OnNormalizeLabelChanged);
                ConfigSlider(_normalizeMaxSlider, -1000f, 4000f, _controller.NormalizeMax, OnNormalizeLabelChanged);
            }

            if (_applyThresholdButton != null) _applyThresholdButton.onClick.AddListener(OnApplyThreshold);
            if (_applyNormalizeButton != null) _applyNormalizeButton.onClick.AddListener(OnApplyNormalize);

            if (_clipToggle != null)
            {
                _clipToggle.isOn = true;
                _clipToggle.onValueChanged.AddListener(OnClipToggle);
            }
            if (_classColorToggle != null)
            {
                _classColorToggle.isOn = false;
                _classColorToggle.onValueChanged.AddListener(OnClassColorToggle);
            }

            RefreshAppearanceLabels();
            RefreshThresholdLabels();
            RefreshNormalizeLabels();
        }

        void ConfigSlider(Slider s, float min, float max, float value, UnityEngine.Events.UnityAction<float> cb)
        {
            if (s == null) return;
            s.minValue = min;
            s.maxValue = max;
            s.SetValueWithoutNotify(value);
            s.onValueChanged.AddListener(cb);
        }

        // === 外观-实时回调 ===
        void OnPointSizeChanged(float v)
        {
            if (_pointCloud != null) _pointCloud.SetPointSize(v);
            RefreshAppearanceLabels();
        }

        void OnWindowChanged(float v)
        {
            if (_windowLevel != null && _windowCenterSlider != null && _windowWidthSlider != null)
                _windowLevel.SetWindow(_windowCenterSlider.value, _windowWidthSlider.value);
            RefreshAppearanceLabels();
        }

        void OnTintChanged(float v)
        {
            if (_gainSlider != null) _gain = _gainSlider.value;
            if (_tintRSlider != null) _tintR = _tintRSlider.value;
            if (_tintGSlider != null) _tintG = _tintGSlider.value;
            if (_tintBSlider != null) _tintB = _tintBSlider.value;
            ApplyTint();
            RefreshAppearanceLabels();
        }

        // === 点生成-滑块仅改标签，Apply 才重建(防抖) ===
        void OnThresholdLabelChanged(float v) => RefreshThresholdLabels();
        void OnNormalizeLabelChanged(float v) => RefreshNormalizeLabels();

        void OnApplyThreshold()
        {
            if (_controller != null && _thresholdMinSlider != null && _thresholdMaxSlider != null)
                _controller.SetThreshold(_thresholdMinSlider.value, _thresholdMaxSlider.value);
        }

        void OnApplyNormalize()
        {
            if (_controller != null && _normalizeMinSlider != null && _normalizeMaxSlider != null)
                _controller.SetNormalize(_normalizeMinSlider.value, _normalizeMaxSlider.value);
        }

        // === 开关 ===
        void OnClipToggle(bool on)
        {
            if (_clipping != null) _clipping.SetEnabled(on);
        }

        void OnClassColorToggle(bool on)
        {
            if (_controller != null) _controller.SetColorMode(on);
        }

        // === 状态刷新 ===
        void OnReportChanged(DicomLoadReport r) => RefreshStatus(r);

        void RefreshStatus(DicomLoadReport r)
        {
            if (_statusText == null || r == null) return;

            if (r.HasError)
            {
                _statusText.text = $"加载失败\n阶段: {r.PhaseText}\n{r.ErrorMessage}";
                SetProgress(0f);
                return;
            }

            string text = $"阶段: {r.PhaseText}";
            float ratio = 0f;

            if (r.Phase == DicomLoadPhase.Parsing && r.FilesTotal > 0)
            {
                text += $"\n文件: {r.FilesDone}/{r.FilesTotal}  {r.CurrentFile}";
                ratio = r.FileRatio;
            }

            if (r.Phase == DicomLoadPhase.Completed || r.Phase == DicomLoadPhase.BuildingPoints)
            {
                text += $"\n体积: {r.Width} x {r.Height} x {r.Depth}";
                text += $"\n点数: {r.PointCount:N0}";
                text += $"\n加载: {r.LoadSeconds:F2}s  建点: {r.BuildSeconds:F2}s";
                ratio = 1f;

                if (r.Phase == DicomLoadPhase.Completed && r.PointCount == 0)
                    text += "\n阈值过滤后无可见点，尝试放宽阈值";
            }

            _statusText.text = text;
            SetProgress(ratio);
        }

        void SetProgress(float ratio)
        {
            if (_progressFill == null) return;
            _progressFill.fillAmount = Mathf.Clamp01(ratio);
        }

        // === 标签刷新 ===
        void RefreshAppearanceLabels()
        {
            if (_pointSizeLabel != null && _pointSizeSlider != null) _pointSizeLabel.text = $"点大小: {_pointSizeSlider.value:F4}";
            if (_windowCenterLabel != null && _windowCenterSlider != null) _windowCenterLabel.text = $"窗位: {_windowCenterSlider.value:F2}";
            if (_windowWidthLabel != null && _windowWidthSlider != null) _windowWidthLabel.text = $"窗宽: {_windowWidthSlider.value:F2}";
            if (_gainLabel != null) _gainLabel.text = $"增益: {_gain:F2}";
        }

        void RefreshThresholdLabels()
        {
            if (_thresholdMinLabel != null && _thresholdMinSlider != null) _thresholdMinLabel.text = $"阈值下限: {_thresholdMinSlider.value:F0}";
            if (_thresholdMaxLabel != null && _thresholdMaxSlider != null) _thresholdMaxLabel.text = $"阈值上限: {_thresholdMaxSlider.value:F0}";
        }

        void RefreshNormalizeLabels()
        {
            if (_normalizeMinLabel != null && _normalizeMinSlider != null) _normalizeMinLabel.text = $"归一化下限: {_normalizeMinSlider.value:F0}";
            if (_normalizeMaxLabel != null && _normalizeMaxSlider != null) _normalizeMaxLabel.text = $"归一化上限: {_normalizeMaxSlider.value:F0}";
        }

        void ApplyTint() => Shader.SetGlobalVector(_TintId, new Vector4(_tintR, _tintG, _tintB, _gain));
    }
}
