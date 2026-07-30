using System;
using System.Collections.Generic;
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
        // 药物模块(mode3):面板只调它的接口,显色/底图/分析的联动由它广播快照完成
        [SerializeField] GeneDrugController _drug;
        // 基因引导:VR 面板独立触发数据加载与点云激活,不依赖 OnGUI 面板先切标签
        [SerializeField] GeneDemoBootstrap _bootstrap;

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

        [Header("基因搜索")]
        // 关键字显示
        [SerializeField] TextMeshProUGUI _searchKeywordLabel;
        // 字母/数字键:按钮子标签文字即该键字符,运行时提取绑定
        [SerializeField] Button[] _keyButtons;
        [SerializeField] Button _backspaceButton;
        [SerializeField] Button _clearKeywordButton;
        // 匹配结果列表容器(主滚动区内,VerticalLayoutGroup)
        [SerializeField] RectTransform _searchResultContent;
        // 结果项按钮模板:工厂建好,运行时 SetActive(false) 并 Instantiate 复用
        [SerializeField] Button _searchResultTemplate;
        [SerializeField] TextMeshProUGUI _searchResultCountLabel;

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

        [Header("药物作用 (mode3)")]
        // 药物按钮槽(工厂预建固定数量),点击给药;剂量滑条驱动整体显色平滑过渡
        [SerializeField] Button[] _drugButtons;
        [SerializeField] TextMeshProUGUI[] _drugButtonLabels;
        [SerializeField] TextMeshProUGUI _drugStateLabel;
        [SerializeField] Slider _drugDoseSlider;
        [SerializeField] TextMeshProUGUI _drugDoseLabel;
        [SerializeField] Button _clearDrugButton;

        [Header("区域结果")]
        [SerializeField] TextMeshProUGUI _regionNameLabel;
        // top N 基因按钮槽(工厂预建固定数量),点击渲染该基因该区域
        [SerializeField] Button[] _topGeneButtons;
        [SerializeField] TextMeshProUGUI[] _topGeneLabels;

        [Header("区域内基因搜索")]
        // 分析后查任意基因在本选区的画取占比(补 Top5 之外),独立于顶部基因搜索
        [SerializeField] TextMeshProUGUI _regionSearchKeywordLabel;
        [SerializeField] Button[] _regionKeyButtons;
        [SerializeField] Button _regionBackspaceButton;
        [SerializeField] Button _regionClearKeywordButton;
        [SerializeField] RectTransform _regionSearchResultContent;
        [SerializeField] Button _regionSearchResultTemplate;
        [SerializeField] TextMeshProUGUI _regionSearchResultCountLabel;

        [Header("模型变换")]
        [SerializeField] Slider _modelScaleSlider;
        [SerializeField] TextMeshProUGUI _modelScaleLabel;
        [SerializeField] Button _resetTransformButton;

        string[] _genes;
        int _geneIdx = -1;
        // 区域模式:false=mode1 整体, true=mode2 区域
        bool _regionMode;

        // 搜索:当前关键字 + 全基因名小写缓存(与 _genes 同序,防每次按键 ToLower 产 GC)
        string _keyword = "";
        string[] _genesLower;
        // 结果按钮对象池,复用模板 Instantiate 的副本
        readonly List<Button> _resultPool = new List<Button>();
        // 结果显示上限,防上万匹配撑爆面板;超出提示缩小范围
        const int MaxResults = 30;

        // 区域分析(异步)状态
        int _selectedCount;
        bool _analyzing;
        volatile bool _analyzeProgressDirty;
        volatile float _bgAnalyzeProgress;
        float _analyzeProgress;
        GeneRegionReport _report;
        // 最近一次 top 基因结果,供按钮点击取基因名
        string[] _topGeneNames;
        // 最近一次分析所依据的药物版本;与当前快照不一致则在区域结果处提示过期
        int _reportDrugRevision;

        // 区域内搜索:当前关键字 + 结果按钮对象池(复用模板 Instantiate 的副本)
        string _regionKeyword = "";
        readonly List<Button> _regionResultPool = new List<Button>();

        bool _dataSourceBound;
        float _autoBindElapsed;
        float _autoBindNextRetry;
        // 是否已请求过数据加载,防区域模式反复开关重复触发 Load
        bool _loadTriggered;

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
            if (_drug != null) _drug.OnStateChanged -= OnDrugStateChanged;
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
            // 先经 Bootstrap 拿控制器:基因点云物体常被 OnGUI 面板切到 DICOM 标签时置为 inactive,
            // FindFirstObjectByType 默认跳过 inactive 会绑不上;Bootstrap 常驻 active 且持有引用
            if (_bootstrap == null) _bootstrap = FindFirstObjectByType<GeneDemoBootstrap>();
            if (_controller == null && _bootstrap != null)
            {
                _bootstrap.Setup();
                _controller = _bootstrap.Controller;
            }
            if (_controller == null) _controller = GetComponentInChildren<GeneColorController>(true);
            // 兜底:含 inactive 也扫,防基因点云被另一面板隐藏时死活绑不上
            if (_controller == null) _controller = FindFirstObjectByType<GeneColorController>(FindObjectsInactive.Include);
            if (_controller == null) return false;

            if (_modelTransform == null)
                _modelTransform = _controller.GetComponent<GeneModelTransform>();
            if (_brush == null)
                _brush = _controller.GetComponent<GeneBrushSelector>();
            if (_drug == null)
                _drug = _controller.GetComponent<GeneDrugController>();
            // 工厂未绑 tag 名表时,复用画笔已注入的同一张,避免区域名回退成 "区域{tag}"
            if (_tagNameTable == null && _brush != null) _tagNameTable = _brush.TagNameTable;

            _controller.OnReportChanged += OnReportChanged;
            _controller.OnGeneChanged += OnGeneChanged;
            _controller.OnLoaded += OnModelLoaded;
            if (_brush != null) _brush.OnSelectionChanged += OnSelectionChanged;
            if (_modelTransform != null) _modelTransform.OnPoseChanged += OnModelPoseChanged;
            if (_drug != null) _drug.OnStateChanged += OnDrugStateChanged;

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

            SetupSearchControls();

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

            SetupDrugControls();
            SetupRegionSearchControls();
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

            // 药物:控制器绑定后才知道药物库内容,此时填按钮文字并配剂量滑条量程
            if (_drug != null)
            {
                ConfigSlider(_drugDoseSlider, 0f, _drug.MaxDose, _drug.TargetDose, OnDrugDoseChanged);
                RefreshDrugButtons();
                RefreshDrugState();
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
            // 进区域模式即确保基因点云已激活并加载数据(VR 面板独立触发,不等 OnGUI 面板切标签)
            if (on) EnsureGeneReady();
            if (_brush != null) _brush.SetEnabled(on && _brushToggle != null && _brushToggle.isOn);
            if (!on && _controller != null) _controller.ClearSelection();
        }

        // 确保基因点云物体 active 且已发起数据加载:画笔 Update 与区域分析都要求 Model 就绪
        // 物体常被 OnGUI 面板切 DICOM 标签时置 inactive,inactive 下 Update 不跑,画笔永不染色
        void EnsureGeneReady()
        {
            if (_controller == null) TryBindDataSource();
            if (_controller == null) return;
            if (!_controller.gameObject.activeSelf) _controller.gameObject.SetActive(true);
            if (_controller.Model != null || _loadTriggered) return;
            // 模型未加载:经 Bootstrap 触发一次加载(幂等,内部已 Setup 过组件)
            if (_bootstrap != null) { _loadTriggered = true; _bootstrap.LoadDefault(); }
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
            BuildGenesLowerCache();
            RefreshGeneLabel();
            // 数据(重)加载后若已有关键字,刷新匹配结果
            if (!string.IsNullOrEmpty(_keyword)) RefreshSearchResults();
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

        // === 基因搜索 ===
        // 绑定虚拟键盘按键、退格/清空、隐藏结果模板;运行时绑因编辑器期监听不持久
        void SetupSearchControls()
        {
            if (_keyButtons != null)
            {
                foreach (var key in _keyButtons)
                {
                    if (key == null) continue;
                    // 键字符取自按钮内 TMP 子标签文字(工厂建按钮时写入)
                    var label = key.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (label == null || string.IsNullOrEmpty(label.text)) continue;
                    char c = label.text[0];
                    key.onClick.AddListener(() => AppendKeyword(c));
                }
            }
            if (_backspaceButton != null) _backspaceButton.onClick.AddListener(OnBackspace);
            if (_clearKeywordButton != null) _clearKeywordButton.onClick.AddListener(OnClearKeyword);
            // 模板不参与显示,运行时靠 Instantiate 复用
            if (_searchResultTemplate != null) _searchResultTemplate.gameObject.SetActive(false);

            RefreshKeywordLabel();
            RefreshSearchResults();
        }

        // 全基因名小写缓存,与 _genes 同序;搜索时只对关键字转一次小写做 Ordinal 匹配
        void BuildGenesLowerCache()
        {
            if (_genes == null) { _genesLower = null; return; }
            _genesLower = new string[_genes.Length];
            for (int i = 0; i < _genes.Length; i++)
                _genesLower[i] = _genes[i].ToLowerInvariant();
        }

        void AppendKeyword(char c)
        {
            _keyword += c;
            RefreshKeywordLabel();
            RefreshSearchResults();
        }

        void OnBackspace()
        {
            if (_keyword.Length == 0) return;
            _keyword = _keyword.Substring(0, _keyword.Length - 1);
            RefreshKeywordLabel();
            RefreshSearchResults();
        }

        void OnClearKeyword()
        {
            if (_keyword.Length == 0) return;
            _keyword = "";
            RefreshKeywordLabel();
            RefreshSearchResults();
        }

        void RefreshKeywordLabel()
        {
            if (_searchKeywordLabel == null) return;
            _searchKeywordLabel.text = string.IsNullOrEmpty(_keyword)
                ? "关键字: (空)"
                : $"关键字: {_keyword}";
        }

        // 增量过滤:关键字子串匹配(大小写不敏感),取前 MaxResults 条填进结果列表
        void RefreshSearchResults()
        {
            if (string.IsNullOrEmpty(_keyword) || _genesLower == null)
            {
                HideAllResultButtons();
                if (_searchResultCountLabel != null) _searchResultCountLabel.text = "";
                return;
            }

            string kw = _keyword.ToLowerInvariant();
            int shown = 0;
            int matched = 0;
            for (int i = 0; i < _genesLower.Length; i++)
            {
                if (_genesLower[i].IndexOf(kw, StringComparison.Ordinal) < 0) continue;
                matched++;
                if (shown >= MaxResults) continue;
                BindResultButton(shown, _genes[i]);
                shown++;
            }

            // 隐藏多余池按钮
            for (int i = shown; i < _resultPool.Count; i++)
                if (_resultPool[i] != null) _resultPool[i].gameObject.SetActive(false);

            if (_searchResultCountLabel != null)
                _searchResultCountLabel.text = matched == 0
                    ? "无匹配基因"
                    : (matched > MaxResults
                        ? $"匹配 {matched} 条,显示前 {MaxResults},输入更多缩小范围"
                        : $"匹配 {matched} 条");
        }

        // 取/建第 idx 个池按钮并绑定为基因 name;先清旧监听防串号
        void BindResultButton(int idx, string name)
        {
            var btn = GetOrCreatePooledButton(idx);
            if (btn == null) return;
            var label = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = name;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnSearchResultClicked(name));
            btn.gameObject.SetActive(true);
        }

        Button GetOrCreatePooledButton(int idx)
        {
            if (_searchResultTemplate == null || _searchResultContent == null) return null;
            while (_resultPool.Count <= idx)
            {
                var clone = Instantiate(_searchResultTemplate, _searchResultContent);
                clone.gameObject.SetActive(false);
                _resultPool.Add(clone);
            }
            return _resultPool[idx];
        }

        void HideAllResultButtons()
        {
            foreach (var b in _resultPool)
                if (b != null) b.gameObject.SetActive(false);
        }

        // 点结果:与 Top5 点击同路径,选中该基因;区域模式下只渲染选区内该基因,不动掩码
        void OnSearchResultClicked(string name)
        {
            if (_controller == null || string.IsNullOrEmpty(name)) return;
            EnsureGeneReady();
            _controller.SelectGene(name);
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
            if (on) EnsureGeneReady();
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
                // 把药物快照与全模型 tag 交给后台:分析结果即药物作用后的反应
                var report = await GeneRegionAnalyzer.AnalyzeAsync(
                    ids, tags, _controller.ExpressionDir, _controller.Model.CellCount, 5,
                    _controller.Drug, _controller.Model.Tag,
                    p => { _bgAnalyzeProgress = p; _analyzeProgressDirty = true; },
                    CancellationToken.None);

                // 区域名查 ScriptableObject 须主线程,await 后已回主线程
                if (_tagNameTable != null)
                {
                    string name = _tagNameTable.GetName(report.DominantTag);
                    if (!string.IsNullOrEmpty(name)) report.RegionName = name;
                }
                _report = report;
                _reportDrugRevision = report.DrugRevision;
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
                    ? $"区域: {_report.RegionName}  (tag {_report.DominantTag}, {_report.CellCount:N0} cell)\n{DescribeReportDrug()}"
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

            // 报告更新(分析完成/清除)后刷新区域搜索:占比换算随新选区,或回到"先分析"提示
            RefreshRegionSearchResults();
        }

        // 结果的用药前提说明 + 过期提示(分析后又改了用药,结果不再对应当前模型状态)
        string DescribeReportDrug()
        {
            if (_report == null) return "";
            string basis = _report.HasDrug
                ? $"用药: {_report.DrugName} 剂量 {_report.DrugDose:F2}"
                : "用药: 无 (基线)";
            bool stale = _drug != null && _drug.Snapshot.Revision != _reportDrugRevision;
            return stale ? basis + "  [用药已变更,请重新分析]" : basis;
        }

        void OnTopGeneClicked(int idx)
        {
            if (_topGeneNames == null || idx < 0 || idx >= _topGeneNames.Length) return;
            // 关画笔清 overlay(选区掩码保留),露出区域表达显色
            if (_brush != null) _brush.SetEnabled(false);
            if (_brushToggle != null) _brushToggle.SetIsOnWithoutNotify(false);
            _controller.SelectGene(_topGeneNames[idx]);
        }

        // === 药物作用 (mode3) ===
        // 药物按钮按索引绑定;控制器晚绑定时文字在 SetupControllerControls 里补齐
        void SetupDrugControls()
        {
            if (_drugButtons != null)
            {
                for (int i = 0; i < _drugButtons.Length; i++)
                {
                    if (_drugButtons[i] == null) continue;
                    int idx = i;
                    _drugButtons[i].onClick.AddListener(() => OnDrugClicked(idx));
                    _drugButtons[i].gameObject.SetActive(false);
                }
            }
            if (_clearDrugButton != null) _clearDrugButton.onClick.AddListener(OnClearDrug);
            RefreshDrugState();
        }

        // 填充药物按钮:有几味药显示几个槽,其余隐藏
        void RefreshDrugButtons()
        {
            if (_drugButtons == null) return;
            int n = _drug != null ? _drug.DrugCount : 0;
            for (int i = 0; i < _drugButtons.Length; i++)
            {
                if (_drugButtons[i] == null) continue;
                bool show = i < n;
                _drugButtons[i].gameObject.SetActive(show);
                if (!show) continue;
                if (_drugButtonLabels != null && i < _drugButtonLabels.Length && _drugButtonLabels[i] != null)
                {
                    bool cur = i == _drug.CurrentIndex;
                    _drugButtonLabels[i].text = (cur ? "● " : "○ ") + _drug.Profile.GetName(i);
                }
            }
        }

        void OnDrugClicked(int idx)
        {
            if (_drug == null) return;
            // 给药需要点云已就绪(表达值要重算),与选基因同路径先确保加载
            EnsureGeneReady();
            _drug.SelectDrug(idx);
            // 换药后量程可能变(各药 MaxDose 不同),重配滑条
            if (_drugDoseSlider != null)
            {
                _drugDoseSlider.maxValue = _drug.MaxDose;
                _drugDoseSlider.SetValueWithoutNotify(_drug.TargetDose);
            }
            RefreshDrugButtons();
            RefreshDrugState();
        }

        void OnDrugDoseChanged(float v)
        {
            if (_drug != null) _drug.SetDose(v);
            RefreshDrugState();
        }

        void OnClearDrug()
        {
            if (_drug == null) return;
            _drug.ClearDrug();
            if (_drugDoseSlider != null) _drugDoseSlider.SetValueWithoutNotify(0f);
            RefreshDrugButtons();
            RefreshDrugState();
        }

        // 药物快照变化(含过渡中每次步进):刷新读数;过渡结束再校正滑条,过渡中不抢用户手上的滑条
        void OnDrugStateChanged(GeneDrugSnapshot snapshot)
        {
            RefreshDrugState();
            RefreshDrugButtons();
            if (_drug != null && !_drug.IsTransitioning && _drugDoseSlider != null)
                _drugDoseSlider.SetValueWithoutNotify(_drug.TargetDose);
            // 分析结果的用药前提可能已变,区域结果处需重刷过期提示
            RefreshRegionResult();
        }

        void RefreshDrugState()
        {
            if (_drugStateLabel != null)
            {
                if (_drug == null) _drugStateLabel.text = "药物: 未绑定";
                else if (_drug.DrugCount == 0) _drugStateLabel.text = "药物: 未配置药物库";
                else if (_drug.CurrentDrug == null) _drugStateLabel.text = "药物: 未用药 (基线表达)";
                else _drugStateLabel.text = $"药物: {_drug.CurrentDrugName}" + (_drug.IsTransitioning ? "  (过渡中)" : "");
            }
            if (_drugDoseLabel != null)
                _drugDoseLabel.text = _drug != null && _drug.CurrentDrug != null
                    ? $"剂量: {_drug.CurrentDose:F2} / {_drug.MaxDose:F2}"
                    : "剂量: -";
        }

        // === 区域内基因搜索(查任意基因画取占比) ===
        // 绑定虚拟键盘、退格/清空、隐藏结果模板;运行时绑因编辑器期监听不持久
        void SetupRegionSearchControls()
        {
            if (_regionKeyButtons != null)
            {
                foreach (var key in _regionKeyButtons)
                {
                    if (key == null) continue;
                    var label = key.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (label == null || string.IsNullOrEmpty(label.text)) continue;
                    char c = label.text[0];
                    key.onClick.AddListener(() => AppendRegionKeyword(c));
                }
            }
            if (_regionBackspaceButton != null) _regionBackspaceButton.onClick.AddListener(OnRegionBackspace);
            if (_regionClearKeywordButton != null) _regionClearKeywordButton.onClick.AddListener(OnRegionClearKeyword);
            if (_regionSearchResultTemplate != null) _regionSearchResultTemplate.gameObject.SetActive(false);

            RefreshRegionKeywordLabel();
            RefreshRegionSearchResults();
        }

        void AppendRegionKeyword(char c)
        {
            _regionKeyword += c;
            RefreshRegionKeywordLabel();
            RefreshRegionSearchResults();
        }

        void OnRegionBackspace()
        {
            if (_regionKeyword.Length == 0) return;
            _regionKeyword = _regionKeyword.Substring(0, _regionKeyword.Length - 1);
            RefreshRegionKeywordLabel();
            RefreshRegionSearchResults();
        }

        void OnRegionClearKeyword()
        {
            if (_regionKeyword.Length == 0) return;
            _regionKeyword = "";
            RefreshRegionKeywordLabel();
            RefreshRegionSearchResults();
        }

        void RefreshRegionKeywordLabel()
        {
            if (_regionSearchKeywordLabel == null) return;
            _regionSearchKeywordLabel.text = string.IsNullOrEmpty(_regionKeyword)
                ? "关键字: (空)"
                : $"关键字: {_regionKeyword}";
        }

        // 增量过滤:关键字子串匹配全基因名,结果按钮显示"基因名  画取占比%";未分析提示先分析
        void RefreshRegionSearchResults()
        {
            if (_report == null)
            {
                HideAllRegionResultButtons();
                if (_regionSearchResultCountLabel != null) _regionSearchResultCountLabel.text = "请先分析区域";
                return;
            }
            if (string.IsNullOrEmpty(_regionKeyword) || _genesLower == null)
            {
                HideAllRegionResultButtons();
                if (_regionSearchResultCountLabel != null) _regionSearchResultCountLabel.text = "";
                return;
            }

            string kw = _regionKeyword.ToLowerInvariant();
            int shown = 0;
            int matched = 0;
            for (int i = 0; i < _genesLower.Length; i++)
            {
                if (_genesLower[i].IndexOf(kw, StringComparison.Ordinal) < 0) continue;
                matched++;
                if (shown >= MaxResults) continue;
                BindRegionResultButton(shown, _genes[i]);
                shown++;
            }

            for (int i = shown; i < _regionResultPool.Count; i++)
                if (_regionResultPool[i] != null) _regionResultPool[i].gameObject.SetActive(false);

            if (_regionSearchResultCountLabel != null)
                _regionSearchResultCountLabel.text = matched == 0
                    ? "无匹配基因"
                    : (matched > MaxResults
                        ? $"匹配 {matched} 条,显示前 {MaxResults},输入更多缩小范围"
                        : $"匹配 {matched} 条");
        }

        // 取/建第 idx 个池按钮,文字为"基因名  占比%",点击选中该基因(同 Top5 路径)
        void BindRegionResultButton(int idx, string name)
        {
            var btn = GetOrCreateRegionPooledButton(idx);
            if (btn == null) return;
            var label = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = $"{name}  {FormatPaintFraction(name)}";
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnRegionSearchResultClicked(name));
            btn.gameObject.SetActive(true);
        }

        // 画取占比文本:-1(全模型无表达)显示"无表达",否则百分比
        string FormatPaintFraction(string gene)
        {
            if (_report == null || !_report.PaintFractions.TryGetValue(gene, out float frac))
                return "-";
            return frac < 0f ? "无表达" : $"{frac * 100f:F1}%";
        }

        Button GetOrCreateRegionPooledButton(int idx)
        {
            if (_regionSearchResultTemplate == null || _regionSearchResultContent == null) return null;
            while (_regionResultPool.Count <= idx)
            {
                var clone = Instantiate(_regionSearchResultTemplate, _regionSearchResultContent);
                clone.gameObject.SetActive(false);
                _regionResultPool.Add(clone);
            }
            return _regionResultPool[idx];
        }

        void HideAllRegionResultButtons()
        {
            foreach (var b in _regionResultPool)
                if (b != null) b.gameObject.SetActive(false);
        }

        // 点结果:与 Top5 点击同路径,关画笔露出区域表达显色
        void OnRegionSearchResultClicked(string name)
        {
            if (_controller == null || string.IsNullOrEmpty(name)) return;
            if (_brush != null) _brush.SetEnabled(false);
            if (_brushToggle != null) _brushToggle.SetIsOnWithoutNotify(false);
            _controller.SelectGene(name);
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
