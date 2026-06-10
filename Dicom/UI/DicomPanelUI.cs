using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Dicom.Core;
using Dicom.Analysis;
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

        [Header("数据源自动配置")]
        // 点云物体常由 DicomDemoBootstrap 运行时动态创建,面板 Start 时可能尚不存在
        // 开启后未绑定数据源时全场景查找 PointCloudController,查不到则按间隔重试直到超时
        [SerializeField] bool _autoBindDataSource = true;
        [SerializeField] float _autoBindRetryInterval = 0.5f;
        [SerializeField] float _autoBindTimeout = 15f;

        [Header("全局字体")]
        // 非空则运行时把面板内所有 TextMeshProUGUI 统一为此字体(中文显示需中文 TMP 字体)
        [SerializeField] TMP_FontAsset _globalFont;

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
        [SerializeField] Toggle _lutColorToggle;
        [SerializeField] Toggle _breakpointColorToggle;
        [SerializeField] Button _lutPresetButton;
        [SerializeField] TextMeshProUGUI _lutPresetLabel;

        [Header("HU 区间分析")]
        [SerializeField] TextMeshProUGUI _huRangeText;
        [SerializeField] Button _applyHuRangeButton;
        [SerializeField] TextMeshProUGUI _huApplyHintLabel;

        float _tintR = 1f, _tintG = 1f, _tintB = 1f, _gain = 1f;

        // 数据源自动绑定重试计时
        bool _dataSourceBound;
        float _autoBindElapsed;
        float _autoBindNextRetry;

        static readonly int _TintId = Shader.PropertyToID("_DicomTint");

        void Start()
        {
            // 统一字体放最前,后续动态刷新的标签文本也沿用工厂创建时的字体
            ApplyGlobalFont();

            SetupControls();
            ApplyTint();

            // 先尝试显式绑定或子物体查找;未果且开启自动配置时由 Update 全场景重试
            if (!TryBindDataSource())
            {
                if (!_autoBindDataSource)
                    Debug.LogWarning("DicomPanelUI 未绑定 PointCloudController 且未开启自动配置");
            }
        }

        void OnDestroy()
        {
            if (_controller != null)
            {
                _controller.OnReportChanged -= OnReportChanged;
                _controller.OnHuAnalyzed -= OnHuAnalyzed;
            }
        }

        void Update()
        {
            if (_dataSourceBound || !_autoBindDataSource) return;

            _autoBindElapsed += Time.unscaledDeltaTime;
            if (_autoBindElapsed >= _autoBindNextRetry)
            {
                _autoBindNextRetry = _autoBindElapsed + _autoBindRetryInterval;
                if (TryBindDataSource()) return;
                if (_autoBindElapsed >= _autoBindTimeout)
                {
                    _autoBindDataSource = false;
                    Debug.LogWarning($"DicomPanelUI 自动配置数据源超时({_autoBindTimeout}s),场景中未找到 PointCloudController");
                }
            }
        }

        // 解析数据源:显式绑定 > 子物体查找 > 全场景查找。成功绑定 controller 即视为完成
        // 返回 true 表示已找到 controller 并完成事件挂接与首次刷新
        bool TryBindDataSource()
        {
            if (_controller == null) _controller = GetComponentInChildren<PointCloudController>();
            if (_controller == null) _controller = FindFirstObjectByType<PointCloudController>();
            if (_controller == null) return false;

            // controller 找到后,其余组件优先取同物体,退而求面板子物体
            if (_pointCloud == null)
            {
                _pointCloud = _controller.GetComponent<DicomPointCloud>();
                if (_pointCloud == null) _pointCloud = GetComponentInChildren<DicomPointCloud>();
            }
            if (_windowLevel == null)
            {
                _windowLevel = _controller.GetComponent<WindowLevelController>();
                if (_windowLevel == null) _windowLevel = GetComponentInChildren<WindowLevelController>();
            }
            if (_clipping == null)
            {
                _clipping = _controller.GetComponent<ClippingPlaneController>();
                if (_clipping == null) _clipping = GetComponentInChildren<ClippingPlaneController>();
            }

            BindControllerEvents();
            // 数据源到位后重新配置依赖 controller 的滑块范围与初值
            SetupControllerControls();
            _dataSourceBound = true;
            return true;
        }

        // 挂接 controller 事件并做首次状态/HU 刷新,初始化显色模式避免 shader 全局变量残留
        void BindControllerEvents()
        {
            _controller.OnReportChanged += OnReportChanged;
            _controller.OnHuAnalyzed += OnHuAnalyzed;
            // 初始化显色模式,否则 shader _DicomColorMode 默认 0 始终走灰度,profile 颜色不生效
            _controller.SetColorMode(DicomColorMode.Intensity);
            RefreshLutPresetLabel();
            RefreshStatus(_controller.Report);
            RefreshHuRange(_controller.HuReport);
        }

        // 把面板内所有文本统一为指定字体;字段为空则不改动,保留预制体原字体
        void ApplyGlobalFont()
        {
            if (_globalFont == null) return;
            var texts = GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var t in texts) t.font = _globalFont;
        }

        // 设定滑块范围与初值并挂回调，范围在此集中管理避免工厂硬编码
        // 不依赖 controller 的控件在此一次性配置;依赖 controller 的见 SetupControllerControls
        void SetupControls()
        {
            ConfigSlider(_windowCenterSlider, 0f, 1f, 0.5f, OnWindowChanged);
            ConfigSlider(_windowWidthSlider, 0.01f, 1f, 1f, OnWindowChanged);
            ConfigSlider(_gainSlider, 0.1f, 4f, 1f, OnTintChanged);
            ConfigSlider(_tintRSlider, 0f, 1f, 1f, OnTintChanged);
            ConfigSlider(_tintGSlider, 0f, 1f, 1f, OnTintChanged);
            ConfigSlider(_tintBSlider, 0f, 1f, 1f, OnTintChanged);

            if (_applyThresholdButton != null) _applyThresholdButton.onClick.AddListener(OnApplyThreshold);
            if (_applyNormalizeButton != null) _applyNormalizeButton.onClick.AddListener(OnApplyNormalize);
            if (_applyHuRangeButton != null) _applyHuRangeButton.onClick.AddListener(OnApplyHuRange);

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
            if (_lutColorToggle != null)
            {
                _lutColorToggle.isOn = false;
                _lutColorToggle.onValueChanged.AddListener(OnLutColorToggle);
            }
            if (_breakpointColorToggle != null)
            {
                _breakpointColorToggle.isOn = false;
                _breakpointColorToggle.onValueChanged.AddListener(OnBreakpointColorToggle);
            }
            if (_lutPresetButton != null) _lutPresetButton.onClick.AddListener(OnCycleLutPreset);

            RefreshAppearanceLabels();
            if (_huApplyHintLabel != null) _huApplyHintLabel.text = "";
        }

        // 依赖 controller 的控件:点大小取决于点云组件,阈值/归一化初值取自 controller
        // 数据源绑定成功后调用,可重复调用(自动配置场景下首帧未绑定,后续帧才执行)
        void SetupControllerControls()
        {
            if (_pointCloud != null)
                ConfigSlider(_pointSizeSlider, 0.0001f, 0.02f, 0.002f, OnPointSizeChanged);

            if (_controller != null)
            {
                ConfigSlider(_thresholdMinSlider, -1000f, 3000f, _controller.ThresholdMin, OnThresholdLabelChanged);
                ConfigSlider(_thresholdMaxSlider, -1000f, 4000f, _controller.ThresholdMax, OnThresholdLabelChanged);
                ConfigSlider(_normalizeMinSlider, -1000f, 3000f, _controller.NormalizeMin, OnNormalizeLabelChanged);
                ConfigSlider(_normalizeMaxSlider, -1000f, 4000f, _controller.NormalizeMax, OnNormalizeLabelChanged);
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

        // 分类/LUT/断点三个 Toggle 互斥,组合出四态:都关=灰度,分类开=分类着色,LUT 开=离散伪彩,断点开=断点插值
        void OnClassColorToggle(bool on)
        {
            if (_controller == null) return;
            if (on)
            {
                if (_lutColorToggle != null) _lutColorToggle.SetIsOnWithoutNotify(false);
                if (_breakpointColorToggle != null) _breakpointColorToggle.SetIsOnWithoutNotify(false);
            }
            _controller.SetColorMode(on ? DicomColorMode.Classification : DicomColorMode.Intensity);
        }

        void OnLutColorToggle(bool on)
        {
            if (_controller == null) return;
            if (on)
            {
                if (_classColorToggle != null) _classColorToggle.SetIsOnWithoutNotify(false);
                if (_breakpointColorToggle != null) _breakpointColorToggle.SetIsOnWithoutNotify(false);
            }
            _controller.SetColorMode(on ? DicomColorMode.Lut : DicomColorMode.Intensity);
        }

        void OnBreakpointColorToggle(bool on)
        {
            if (_controller == null) return;
            if (on)
            {
                if (_classColorToggle != null) _classColorToggle.SetIsOnWithoutNotify(false);
                if (_lutColorToggle != null) _lutColorToggle.SetIsOnWithoutNotify(false);
            }
            _controller.SetColorMode(on ? DicomColorMode.Breakpoint : DicomColorMode.Intensity);
        }

        // 循环切换 LUT 预设并重新烘焙上传,仅在 LUT 模式可见时有意义
        void OnCycleLutPreset()
        {
            if (_controller == null) return;
            var profile = _controller.LutProfile;
            if (profile == null) return;

            int count = System.Enum.GetValues(typeof(DicomLutProfile.LutPreset)).Length;
            // 跳过 Custom(索引 0),在内置预设间循环
            int next = (int)profile.Preset + 1;
            if (next >= count) next = 1;
            profile.SetPreset((DicomLutProfile.LutPreset)next);
            _controller.SetLutProfile(profile);
            RefreshLutPresetLabel();
        }

        void RefreshLutPresetLabel()
        {
            if (_lutPresetLabel == null || _controller == null || _controller.LutProfile == null) return;
            _lutPresetLabel.text = $"LUT 预设: {_controller.LutProfile.Preset}";
        }

        // === 状态刷新 ===
        void OnReportChanged(DicomLoadReport r) => RefreshStatus(r);

        // === HU 区间分析 ===
        // 加载完成后 controller 自动统计 HU 占用区间,在此刷新展示
        void OnHuAnalyzed(HuRangeReport hu) => RefreshHuRange(hu);

        // 列出自动识别的占用 HU 区间(区间/体素数/占比),供确认与一键写入分类配置
        void RefreshHuRange(HuRangeReport hu)
        {
            if (_huRangeText == null) return;

            if (hu == null || hu.TotalVoxels == 0)
            {
                _huRangeText.text = "加载完成后自动统计";
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"识别到 {hu.Segments.Count} 个占用区间:");
            for (int i = 0; i < hu.Segments.Count; i++)
            {
                var s = hu.Segments[i];
                sb.AppendLine($"[{s.HuMin:F0}, {s.HuMax:F0})  {s.VoxelCount:N0}  {s.Fraction * 100f:F1}%");
            }
            _huRangeText.text = sb.ToString();
        }

        // 一键把自动识别的区间写入分类 Profile,生成区分色并重建点云
        void OnApplyHuRange()
        {
            if (_huApplyHintLabel == null) return;
            if (_controller == null)
            {
                _huApplyHintLabel.text = "未绑定数据源,无法写入";
                return;
            }
            bool ok = _controller.ApplyDetectedRangesToProfile();
            _huApplyHintLabel.text = ok ? "已写入分类配置,可在 Inspector 微调颜色" : "未绑定 Profile 或无区间,无法写入";
        }

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
