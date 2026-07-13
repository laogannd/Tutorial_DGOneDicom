using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Dicom.Core;

namespace Dicom.Gene
{
    // 世界空间 UGUI 基因操作面板,功能对齐 UnifiedDebugPanel 的基因标签页,供 VR 手交互
    // 控件引用由 GenePanelFactory 编辑器工厂绑定;兼容射线与 UIPokeBridge 手指触碰
    // 点云物体由 GeneDemoBootstrap 运行时动态创建,面板 Start 时可能尚不存在,故自动重试绑定
    [AddComponentMenu("Dicom/Gene Panel UI")]
    public class GenePanelUI : MonoBehaviour
    {
        [Header("数据源")]
        [SerializeField] GeneColorController _controller;
        [SerializeField] GeneModelTransform _modelTransform;
        [SerializeField] GeneBrushSelector _brush;
        [SerializeField] GeneTagNameTable _tagNameTable;

        [Header("数据源自动配置")]
        [SerializeField] bool _autoBindDataSource = true;
        [SerializeField] float _autoBindRetryInterval = 0.5f;
        [SerializeField] float _autoBindTimeout = 15f;

        [Header("全局字体")]
        [SerializeField] TMP_FontAsset _globalFont;

        [Header("状态")]
        [SerializeField] TextMeshProUGUI _statusText;
        [SerializeField] Image _progressFill;

        [Header("模式")]
        [SerializeField] Toggle _regionModeToggle;

        [Header("基因选择")]
        [SerializeField] Button _prevGeneButton;
        [SerializeField] Button _nextGeneButton;
        [SerializeField] TextMeshProUGUI _geneLabel;

        [Header("Colormap")]
        [SerializeField] Button _lutPresetButton;
        [SerializeField] TextMeshProUGUI _lutPresetLabel;

        [Header("空间画笔 (mode2)")]
        [SerializeField] Toggle _brushToggle;
        [SerializeField] Button _clearButton;
        [SerializeField] Button _analyzeButton;
        [SerializeField] TextMeshProUGUI _selectionLabel;
        // 笔刷半径(世界米):球形笔刷伸进点云扫过染色的作用半径
        [SerializeField] Slider _brushRadiusSlider;
        [SerializeField] TextMeshProUGUI _brushRadiusLabel;

        [Header("区域结果")]
        [SerializeField] TextMeshProUGUI _regionNameLabel;
        // top N 基因按钮槽(工厂预建固定数量),点击渲染该基因该区域
        [SerializeField] Button[] _topGeneButtons;
        [SerializeField] TextMeshProUGUI[] _topGeneLabels;

        [Header("模型变换")]
        [SerializeField] Slider _modelScaleSlider;
        [SerializeField] TextMeshProUGUI _modelScaleLabel;
        [SerializeField] Button _resetTransformButton;

        string[] _genes;
        int _geneIdx = -1;
        // 区域模式:false=mode1 整体, true=mode2 区域
        bool _regionMode;

        // 区域分析(异步)状态
        int _selectedCount;
        bool _analyzing;
        volatile bool _analyzeProgressDirty;
        volatile float _bgAnalyzeProgress;
        float _analyzeProgress;
        GeneRegionReport _report;
        // 最近一次 top 基因结果,供按钮点击取基因名
        string[] _topGeneNames;

        bool _dataSourceBound;
        float _autoBindElapsed;
        float _autoBindNextRetry;

        void Start()
        {
            ApplyGlobalFont();
            SetupControls();

            if (!TryBindDataSource() && !_autoBindDataSource)
                Debug.LogWarning("GenePanelUI 未绑定 GeneColorController 且未开启自动配置");
        }

        void OnDestroy()
        {
            if (_controller != null)
            {
                _controller.OnReportChanged -= OnReportChanged;
                _controller.OnGeneChanged -= OnGeneChanged;
                _controller.OnLoaded -= OnModelLoaded;
            }
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
            if (_modelTransform != null) _modelTransform.OnPoseChanged -= OnModelPoseChanged;
        }

        void Update()
        {
            // 后台分析进度回主线程展示
            if (_analyzeProgressDirty)
            {
                _analyzeProgressDirty = false;
                _analyzeProgress = _bgAnalyzeProgress;
                RefreshSelectionLabel();
            }

            if (_dataSourceBound || !_autoBindDataSource) return;
            _autoBindElapsed += Time.unscaledDeltaTime;
            if (_autoBindElapsed >= _autoBindNextRetry)
            {
                _autoBindNextRetry = _autoBindElapsed + _autoBindRetryInterval;
                if (TryBindDataSource()) return;
                if (_autoBindElapsed >= _autoBindTimeout)
                {
                    _autoBindDataSource = false;
                    Debug.LogWarning($"GenePanelUI 自动配置数据源超时({_autoBindTimeout}s)");
                }
            }
        }

        bool TryBindDataSource()
        {
            if (_controller == null) _controller = GetComponentInChildren<GeneColorController>();
            if (_controller == null) _controller = FindFirstObjectByType<GeneColorController>();
            if (_controller == null) return false;

            if (_modelTransform == null)
                _modelTransform = _controller.GetComponent<GeneModelTransform>();
            if (_brush == null)
                _brush = _controller.GetComponent<GeneBrushSelector>();

            _controller.OnReportChanged += OnReportChanged;
            _controller.OnGeneChanged += OnGeneChanged;
            _controller.OnLoaded += OnModelLoaded;
            if (_brush != null) _brush.OnSelectionChanged += OnSelectionChanged;
            if (_modelTransform != null) _modelTransform.OnPoseChanged += OnModelPoseChanged;

            RefreshLutPresetLabel();
            RefreshStatus(_controller.Report);
            // 已加载则立即列基因(晚绑定场景)
            if (_controller.Model != null) OnModelLoaded(_controller.Model);
            SetupControllerControls();
            _dataSourceBound = true;
            return true;
        }

        void SetupControls()
        {
            if (_regionModeToggle != null)
            {
                _regionModeToggle.isOn = false;
                _regionModeToggle.onValueChanged.AddListener(OnRegionModeToggle);
            }
            if (_prevGeneButton != null) _prevGeneButton.onClick.AddListener(OnPrevGene);
            if (_nextGeneButton != null) _nextGeneButton.onClick.AddListener(OnNextGene);
            if (_lutPresetButton != null) _lutPresetButton.onClick.AddListener(OnCycleLutPreset);

            if (_brushToggle != null)
            {
                _brushToggle.isOn = false;
                _brushToggle.onValueChanged.AddListener(OnBrushToggle);
            }
            if (_clearButton != null) _clearButton.onClick.AddListener(OnClearSelection);
            if (_analyzeButton != null) _analyzeButton.onClick.AddListener(OnAnalyze);

            // 笔刷半径滑条须等 _brush 绑定后再配(点云由 Bootstrap 运行时创建,Start 时 _brush 可能仍空)
            // 见 SetupControllerControls

            if (_resetTransformButton != null) _resetTransformButton.onClick.AddListener(OnResetTransform);

            // top 基因按钮:按索引绑定点击,初始隐藏
            if (_topGeneButtons != null)
            {
                for (int i = 0; i < _topGeneButtons.Length; i++)
                {
                    int idx = i;
                    if (_topGeneButtons[i] != null)
                    {
                        _topGeneButtons[i].onClick.AddListener(() => OnTopGeneClicked(idx));
                        _topGeneButtons[i].gameObject.SetActive(false);
                    }
                }
            }

            RefreshRegionResult();
        }

        void SetupControllerControls()
        {
            if (_modelTransform != null)
                ConfigSlider(_modelScaleSlider, _modelTransform.MinScale, _modelTransform.MaxScale,
                    _modelTransform.CurrentScale, OnModelScaleChanged);
            RefreshModelScaleLabel();

            // 笔刷半径滑条:_brush 绑定后配置 min/max/值与监听,补上 Start 时因 _brush 为空漏配的连线
            if (_brush != null)
            {
                ConfigSlider(_brushRadiusSlider, _brush.MinRadius, _brush.MaxRadius, _brush.BrushRadius, OnBrushRadiusChanged);
                RefreshBrushRadiusLabel();
            }
        }

        void ConfigSlider(Slider s, float min, float max, float value, UnityEngine.Events.UnityAction<float> cb)
        {
            if (s == null) return;
            s.minValue = min;
            s.maxValue = max;
            s.SetValueWithoutNotify(value);
            s.onValueChanged.AddListener(cb);
        }

        void ApplyGlobalFont()
        {
            if (_globalFont == null) return;
            foreach (var t in GetComponentsInChildren<TextMeshProUGUI>(true)) t.font = _globalFont;
        }

        // === 模式 ===
        void OnRegionModeToggle(bool on)
        {
            _regionMode = on;
            if (_brush != null) _brush.SetEnabled(on && _brushToggle != null && _brushToggle.isOn);
            if (!on && _controller != null) _controller.ClearSelection();
        }

        // === 基因选择:循环切换 ===
        void OnPrevGene() => CycleGene(-1);
        void OnNextGene() => CycleGene(1);

        void CycleGene(int dir)
        {
            if (_controller == null || _genes == null || _genes.Length == 0) return;
            _geneIdx = ((_geneIdx + dir) % _genes.Length + _genes.Length) % _genes.Length;
            _controller.SelectGene(_genes[_geneIdx]);
        }

        void OnModelLoaded(GeneModelData model)
        {
            _genes = GeneRepository.ListGenes(_controller.ExpressionDir);
            RefreshGeneLabel();
        }

        void OnGeneChanged(string geneName)
        {
            if (_genes != null)
                for (int i = 0; i < _genes.Length; i++)
                    if (_genes[i] == geneName) { _geneIdx = i; break; }
            RefreshGeneLabel();
        }

        void RefreshGeneLabel()
        {
            if (_geneLabel == null) return;
            string cur = _controller != null ? _controller.CurrentGeneName : "";
            int total = _genes != null ? _genes.Length : 0;
            _geneLabel.text = string.IsNullOrEmpty(cur)
                ? $"基因: (未选)  共{total}"
                : $"基因: {cur}  ({_geneIdx + 1}/{total})";
        }

        // === Colormap 预设循环 ===
        void OnCycleLutPreset()
        {
            if (_controller == null) return;
            var profile = _controller.LutProfile;
            if (profile == null) return;
            int count = System.Enum.GetValues(typeof(DicomLutProfile.LutPreset)).Length;
            int next = (int)profile.Preset + 1;
            if (next >= count) next = 1; // 跳过 Custom(0)
            profile.SetPreset((DicomLutProfile.LutPreset)next);
            _controller.SetLutProfile(profile);
            RefreshLutPresetLabel();
        }

        void RefreshLutPresetLabel()
        {
            if (_lutPresetLabel == null || _controller == null || _controller.LutProfile == null) return;
            _lutPresetLabel.text = $"Colormap: {_controller.LutProfile.Preset}";
        }

        // === 画笔 ===
        void OnBrushToggle(bool on)
        {
            if (_brush != null) _brush.SetEnabled(on && _regionMode);
        }

        void OnClearSelection()
        {
            if (_brush != null) _brush.ClearSelection();
            _report = null;
            RefreshRegionResult();
        }

        void OnBrushRadiusChanged(float v)
        {
            if (_brush != null) _brush.BrushRadius = v;
            RefreshBrushRadiusLabel();
        }

        void RefreshBrushRadiusLabel()
        {
            if (_brush == null || _brushRadiusLabel == null) return;
            _brushRadiusLabel.text = $"笔刷半径: {_brush.BrushRadius * 100f:F1} cm";
        }

        void OnSelectionChanged(int count)
        {
            _selectedCount = count;
            RefreshSelectionLabel();
        }

        void RefreshSelectionLabel()
        {
            if (_selectionLabel == null) return;
            if (_analyzing)
                _selectionLabel.text = $"读取基因: {_analyzeProgress * 100f:F0}%";
            else
                _selectionLabel.text = $"已选 cell: {_selectedCount:N0}";
        }

        // === 区域分析 ===
        async void OnAnalyze()
        {
            if (_analyzing || _controller == null || _controller.Model == null) return;
            if (!_controller.CollectSelection(out int[] ids, out int[] tags))
            {
                if (_selectionLabel != null) _selectionLabel.text = "请先用画笔选择区域";
                return;
            }

            _analyzing = true;
            _bgAnalyzeProgress = 0f;
            _analyzeProgress = 0f;
            RefreshSelectionLabel();

            try
            {
                var report = await GeneRegionAnalyzer.AnalyzeAsync(
                    ids, tags, _controller.ExpressionDir, _controller.Model.CellCount, 5,
                    p => { _bgAnalyzeProgress = p; _analyzeProgressDirty = true; },
                    CancellationToken.None);

                // 区域名查 ScriptableObject 须主线程,await 后已回主线程
                if (_tagNameTable != null)
                {
                    string name = _tagNameTable.GetName(report.DominantTag);
                    if (!string.IsNullOrEmpty(name)) report.RegionName = name;
                }
                _report = report;
                RefreshRegionResult();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"区域分析失败: {e.Message}\n{e.StackTrace}");
                if (_selectionLabel != null) _selectionLabel.text = $"分析失败: {e.Message}";
            }
            finally
            {
                _analyzing = false;
                RefreshSelectionLabel();
            }
        }

        // 刷新区域名与 top 基因按钮:填充有结果的槽,其余隐藏
        void RefreshRegionResult()
        {
            if (_regionNameLabel != null)
                _regionNameLabel.text = _report != null
                    ? $"区域: {_report.RegionName}  (tag {_report.DominantTag}, {_report.CellCount:N0} cell)"
                    : "区域: (未分析)";

            int n = _report != null ? _report.TopGenes.Count : 0;
            _topGeneNames = new string[n];
            for (int i = 0; i < n; i++) _topGeneNames[i] = _report.TopGenes[i].Gene;

            if (_topGeneButtons == null) return;
            for (int i = 0; i < _topGeneButtons.Length; i++)
            {
                if (_topGeneButtons[i] == null) continue;
                bool show = i < n;
                _topGeneButtons[i].gameObject.SetActive(show);
                if (show && _topGeneLabels != null && i < _topGeneLabels.Length && _topGeneLabels[i] != null)
                {
                    var g = _report.TopGenes[i];
                    _topGeneLabels[i].text = $"{i + 1}. {g.Gene}  {g.MeanExpression:F3}";
                }
            }
        }

        void OnTopGeneClicked(int idx)
        {
            if (_topGeneNames == null || idx < 0 || idx >= _topGeneNames.Length) return;
            // 关画笔清 overlay(选区掩码保留),露出区域表达显色
            if (_brush != null) _brush.SetEnabled(false);
            if (_brushToggle != null) _brushToggle.SetIsOnWithoutNotify(false);
            _controller.SelectGene(_topGeneNames[idx]);
        }

        // === 模型变换 ===
        void OnModelScaleChanged(float v)
        {
            if (_modelTransform != null) _modelTransform.SetScale(v);
            RefreshModelScaleLabel();
        }

        void OnResetTransform()
        {
            if (_modelTransform == null) return;
            _modelTransform.ResetTransform();
            if (_modelScaleSlider != null) _modelScaleSlider.SetValueWithoutNotify(_modelTransform.CurrentScale);
            RefreshModelScaleLabel();
        }

        void OnModelPoseChanged()
        {
            if (_modelTransform != null && _modelScaleSlider != null)
                _modelScaleSlider.SetValueWithoutNotify(_modelTransform.CurrentScale);
            RefreshModelScaleLabel();
        }

        void RefreshModelScaleLabel()
        {
            if (_modelScaleLabel == null || _modelScaleSlider == null) return;
            _modelScaleLabel.text = $"模型缩放: {_modelScaleSlider.value:F4}";
        }

        // === 状态 ===
        void OnReportChanged(DicomLoadReport r) => RefreshStatus(r);

        void RefreshStatus(DicomLoadReport r)
        {
            if (_statusText == null || r == null) return;
            string text = $"阶段: {r.PhaseText}";
            float ratio = 0f;

            if (r.Phase == DicomLoadPhase.Parsing)
            {
                text += $"\n解析: {r.FileRatio * 100f:F0}%";
                ratio = r.FileRatio;
            }
            if (r.Phase == DicomLoadPhase.Completed)
            {
                text += $"\n网格: {r.Width} x {r.Height} x {r.Depth}";
                text += $"\n渲染点数: {r.PointCount:N0}";
                text += $"\n加载: {r.LoadSeconds:F2}s  建点: {r.BuildSeconds:F2}s";
                ratio = 1f;
            }
            if (r.HasError) text = $"加载失败\n{r.ErrorMessage}";

            _statusText.text = text;
            if (_progressFill != null) _progressFill.fillAmount = Mathf.Clamp01(ratio);
        }
    }
}
