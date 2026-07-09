using System.Collections.Generic;
using UnityEngine;

using Dicom.Core;
using Dicom.Analysis;
using Dicom.PointCloud;
using Dicom.Interaction;
using Dicom.Gene;

namespace Dicom.Demo
{
    // DICOM 与基因表达统一调试面板:顶部标签页切换两大模块,各自内部可折叠分区
    // 切换式共存:同一时刻只激活一个模块点云,切到基因才延迟加载并接管 shader 显色全局变量
    // 挂在场景任意物体上,由 DicomDemoBootstrap / GeneDemoBootstrap 反射绑定各自组件;F1 切换显隐
    public class UnifiedDebugPanel : MonoBehaviour
    {
        // === DICOM 组件组 ===
        [SerializeField] PointCloudController _dicomController;
        [SerializeField] DicomPointCloud _dicomPointCloud;
        [SerializeField] WindowLevelController _windowLevel;
        [SerializeField] ClippingPlaneController _clipping;
        [SerializeField] DicomModelTransform _dicomTransform;

        // === 基因组件组 ===
        [SerializeField] GeneColorController _geneController;
        [SerializeField] GeneModelTransform _geneTransform;
        [SerializeField] GeneBrushSelector _brush;
        [SerializeField] GeneTagNameTable _tagNameTable;
        // 基因延迟加载入口(切到基因标签首次触发)
        [SerializeField] GeneDemoBootstrap _geneBootstrap;

        [SerializeField] bool _visible = true;
        [SerializeField] KeyCode _toggleKey = KeyCode.F1;

        // 0=DICOM 1=基因
        int _activeTab;
        bool _geneLoadRequested;

        static readonly string[] _tabNames = { "DICOM", "基因" };

        // === DICOM 外观状态(初始化时从组件读回) ===
        float _pointSize = 0.002f;
        float _windowCenter = 0.5f;
        float _windowWidth = 1f;
        float _tintR = 1f, _tintG = 1f, _tintB = 1f;
        float _gain = 1f;
        float _thresholdMin = 200f;
        float _thresholdMax = 3000f;
        float _normalizeMin = 200f;
        float _normalizeMax = 1500f;
        bool _clipEnabled = true;
        DicomColorMode _colorMode = DicomColorMode.Classification;
        string _huApplyHint = "";

        static readonly string[] _colorModeNames = { "灰度", "分类", "LUT", "断点" };
        static readonly string[] _lutPresetNames = { "Custom", "热铁", "彩虹", "骨窗", "灰反", "Viridis", "Magma", "Plasma", "Inferno", "Cividis" };
        static readonly string[] _reconstructAxisNames = { "X 轴", "Y 轴", "Z 轴" };

        // === 基因状态 ===
        static readonly string[] _geneModeNames = { "mode1 整体", "mode2 区域" };
        static readonly string[] _brushModeNames = { "球形涂抹", "盒框选" };
        string[] _genes;
        int _selectedGeneIdx = -1;
        // 0=mode1 整体, 1=mode2 区域
        int _geneMode;
        Vector2 _geneScroll;
        int _selectedCount;
        bool _analyzing;
        float _analyzeProgress;
        volatile bool _analyzeProgressDirty;
        volatile float _bgAnalyzeProgress;
        GeneRegionReport _regionReport;
        string _analyzeHint = "";
        // top5 取前几强表达基因
        int _topN = 5;

        // === IMGUI 样式与折叠状态 ===
        Vector2 _scroll;
        GUIStyle _box;
        GUIStyle _title;
        GUIStyle _header;
        GUIStyle _errorBox;
        GUIStyle _foldout;
        bool _stylesReady;
        // 折叠展开状态,一次性建好避免 OnGUI 内 GC
        readonly Dictionary<string, bool> _folds = new Dictionary<string, bool>();

        static readonly int _TintId = Shader.PropertyToID("_DicomTint");

        // === Bootstrap 反射绑定入口 ===
        // DICOM Bootstrap 调用,绑定并把面板切到 DICOM 标签
        public void BindDicom(PointCloudController controller, DicomPointCloud pointCloud,
            WindowLevelController windowLevel, ClippingPlaneController clipping, DicomModelTransform modelTransform)
        {
            _dicomController = controller;
            _dicomPointCloud = pointCloud;
            _windowLevel = windowLevel;
            _clipping = clipping;
            _dicomTransform = modelTransform;

            SyncFromComponents();
            // 绑定发生在 Start 之后时,此处补初始化显色模式与点云激活态
            if (_activeTab == 0 && _dicomController != null) _dicomController.SetColorMode(_colorMode);
            SetModuleActive(_activeTab);
        }

        // 基因 Bootstrap 调用,绑定基因组件并订阅事件;组件由 Bootstrap 延迟创建,此处才具备
        public void BindGene(GeneDemoBootstrap bootstrap, GeneColorController controller,
            GeneModelTransform modelTransform, GeneBrushSelector brush, GeneTagNameTable tagNameTable)
        {
            _geneBootstrap = bootstrap;
            _geneController = controller;
            _geneTransform = modelTransform;
            _brush = brush;
            _tagNameTable = tagNameTable;

            SubscribeGene();
            // 绑定发生在切到基因标签之后,按当前标签校正点云激活态
            SetModuleActive(_activeTab);
        }

        void SubscribeGene()
        {
            if (_geneController != null)
            {
                _geneController.OnLoaded -= OnGeneModelLoaded;
                _geneController.OnLoaded += OnGeneModelLoaded;
                _geneController.OnGeneChanged -= OnGeneChanged;
                _geneController.OnGeneChanged += OnGeneChanged;
            }
            if (_brush != null)
            {
                _brush.OnSelectionChanged -= OnSelectionChanged;
                _brush.OnSelectionChanged += OnSelectionChanged;
            }
        }

        void Start()
        {
            // 未显式绑定时从子物体查找(直接手挂的场景)
            if (_dicomController == null) _dicomController = GetComponentInChildren<PointCloudController>();
            if (_dicomPointCloud == null) _dicomPointCloud = GetComponentInChildren<DicomPointCloud>();
            if (_windowLevel == null) _windowLevel = GetComponentInChildren<WindowLevelController>();
            if (_clipping == null) _clipping = GetComponentInChildren<ClippingPlaneController>();
            if (_dicomTransform == null) _dicomTransform = GetComponentInChildren<DicomModelTransform>();
            if (_geneController == null) _geneController = GetComponentInChildren<GeneColorController>();
            if (_geneTransform == null) _geneTransform = GetComponentInChildren<GeneModelTransform>();
            if (_brush == null) _brush = GetComponentInChildren<GeneBrushSelector>();

            SyncFromComponents();
            ApplyTint();

            // 直接手挂场景(非 Bootstrap 绑定)时,此处已能拿到基因组件,订阅事件;
            // Bootstrap 延迟绑定的情况由 BindGene 再次订阅(幂等)
            SubscribeGene();

            // 初始激活 DICOM 显色,隐藏基因点云
            SetModuleActive(0);
            // 初始化 DICOM 显色模式,否则 shader 全局默认 0 走灰度分支,profile 颜色不生效
            if (_dicomController != null) _dicomController.SetColorMode(_colorMode);
        }

        void OnDestroy()
        {
            if (_geneController != null)
            {
                _geneController.OnLoaded -= OnGeneModelLoaded;
                _geneController.OnGeneChanged -= OnGeneChanged;
            }
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
        }

        // 把当前组件参数读回面板,避免面板默认值覆盖 Inspector 配置
        void SyncFromComponents()
        {
            if (_dicomController != null)
            {
                _thresholdMin = _dicomController.ThresholdMin;
                _thresholdMax = _dicomController.ThresholdMax;
                _normalizeMin = _dicomController.NormalizeMin;
                _normalizeMax = _dicomController.NormalizeMax;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(_toggleKey)) _visible = !_visible;
            // 后台分析进度回主线程展示
            if (_analyzeProgressDirty)
            {
                _analyzeProgressDirty = false;
                _analyzeProgress = _bgAnalyzeProgress;
            }
        }

        // === 标签切换与模块激活 ===
        void SwitchTab(int tab)
        {
            _activeTab = tab;
            SetModuleActive(tab);

            if (tab == 1)
            {
                // 首次切到基因才触发加载
                if (!_geneLoadRequested && _geneBootstrap != null)
                {
                    _geneLoadRequested = true;
                    _geneBootstrap.LoadDefault();
                }
            }
            else
            {
                // 切回 DICOM:重新上传显色全局,夺回 shader 控制权
                if (_dicomController != null) _dicomController.SetColorMode(_colorMode);
                ApplyTint();
            }
        }

        // 激活当前模块点云,隐藏另一个,避免同屏两套点云叠加
        void SetModuleActive(int tab)
        {
            if (_dicomPointCloud != null) _dicomPointCloud.gameObject.SetActive(tab == 0);
            if (_geneController != null) _geneController.gameObject.SetActive(tab == 1);
        }

        void EnsureStyles()
        {
            if (_stylesReady) return;
            _box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8) };
            _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 18, alignment = TextAnchor.MiddleCenter };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            _errorBox = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8), normal = { textColor = Color.white } };
            _foldout = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 14, padding = new RectOffset(8, 8, 5, 5) };
            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            float w = 380f;
            GUILayout.BeginArea(new Rect(10, 10, w, Screen.height - 20), _box);

            var prevTitle = GUI.color;
            GUI.color = new Color(0.4f, 0.8f, 1f, 1f);
            GUILayout.Label("统一调试面板  (F1 显隐)", _title);
            GUI.color = prevTitle;
            GUILayout.Space(4);

            int tab = GUILayout.Toolbar(_activeTab, _tabNames, GUILayout.Height(28));
            if (tab != _activeTab) SwitchTab(tab);
            GUILayout.Space(6);

            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_activeTab == 0) DrawDicomTabs();
            else DrawGeneTabs();
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        // === 可折叠分区 helper ===
        // 画 "▼/▶ 标题" 头,返回是否展开;默认展开由 defaultOpen 控制,只在首次建键时生效
        bool Foldout(string key, string title, bool defaultOpen = true)
        {
            if (!_folds.TryGetValue(key, out bool open))
            {
                open = defaultOpen;
                _folds[key] = open;
            }
            if (GUILayout.Button((open ? "▼ " : "▶ ") + title, _foldout))
            {
                open = !open;
                _folds[key] = open;
            }
            return open;
        }

        // ============================================================
        // DICOM 标签页
        // ============================================================
        void DrawDicomTabs()
        {
            if (Foldout("d_status", "加载状态")) DrawDicomStatus();
            GUILayout.Space(6);
            if (Foldout("d_hu", "HU 区间分析")) DrawHuRange();
            GUILayout.Space(6);
            if (Foldout("d_appear", "外观 (实时)", false)) DrawAppearance();
            GUILayout.Space(6);
            if (Foldout("d_transform", "模型变换", false)) DrawDicomTransform();
            GUILayout.Space(6);
            if (Foldout("d_rebuild", "点生成 (需 Apply)", false)) DrawRebuild();
            GUILayout.Space(6);
            if (Foldout("d_clip", "裁切", false)) DrawClipping();
        }

        void DrawDicomStatus()
        {
            if (_dicomController == null)
            {
                GUILayout.Box("未绑定 PointCloudController", _errorBox);
                return;
            }

            var r = _dicomController.Report;
            if (r.HasError)
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.6f, 0.1f, 0.1f, 0.95f);
                GUILayout.Box($"加载失败\n阶段: {r.PhaseText}\n{r.ErrorMessage}", _errorBox);
                GUI.backgroundColor = prev;
            }
            else
            {
                GUILayout.Label($"阶段: {r.PhaseText}");
            }

            if (r.Phase == DicomLoadPhase.Parsing && r.FilesTotal > 0)
            {
                GUILayout.Label($"文件: {r.FilesDone}/{r.FilesTotal}  {r.CurrentFile}");
                DrawProgressBar(r.FileRatio);
            }
            else if (!string.IsNullOrEmpty(r.CurrentFile) && r.Phase != DicomLoadPhase.Completed)
            {
                GUILayout.Label($"当前: {r.CurrentFile}");
            }

            if (r.Phase == DicomLoadPhase.Completed || r.Phase == DicomLoadPhase.BuildingPoints)
            {
                GUILayout.Label($"体积: {r.Width} x {r.Height} x {r.Depth}");
                GUILayout.Label($"点数: {r.PointCount:N0}");
                GUILayout.Label($"加载耗时: {r.LoadSeconds:F2}s   建点耗时: {r.BuildSeconds:F2}s");

                if (r.Phase == DicomLoadPhase.Completed && r.PointCount == 0)
                {
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.6f, 0.45f, 0.1f, 0.95f);
                    GUILayout.Box("阈值过滤后无可见点，尝试放宽阈值范围", _errorBox);
                    GUI.backgroundColor = prev;
                }
            }
        }

        void DrawHuRange()
        {
            if (_dicomController == null) return;
            var hu = _dicomController.HuReport;
            if (hu == null || hu.TotalVoxels == 0)
            {
                GUILayout.Label("加载完成后自动统计");
                return;
            }

            DrawHistogram(hu);

            GUILayout.Label($"识别到 {hu.Segments.Count} 个占用区间:");
            for (int i = 0; i < hu.Segments.Count; i++)
            {
                var s = hu.Segments[i];
                GUILayout.Label($"  [{s.HuMin:F0}, {s.HuMax:F0})  {s.VoxelCount:N0}  {s.Fraction * 100f:F1}%");
            }

            if (GUILayout.Button("一键应用到 Profile"))
            {
                bool ok = _dicomController.ApplyDetectedRangesToProfile();
                _huApplyHint = ok ? "已写入分类配置,可在 Inspector 微调颜色" : "未绑定 Profile 或无区间,无法写入";
            }
            if (!string.IsNullOrEmpty(_huApplyHint))
                GUILayout.Label(_huApplyHint);
        }

        // 对数刻度柱状图:体素计数跨度极大,线性会被最高峰压平,用 log 拉开层次
        void DrawHistogram(HuRangeReport hu)
        {
            const float height = 70f;
            Rect rect = GUILayoutUtility.GetRect(100, height);
            GUI.Box(rect, GUIContent.none);

            float logMax = Mathf.Log10(hu.MaxBinCount + 1);
            if (logMax <= 0f) return;

            int bins = hu.BinCount;
            float barW = rect.width / bins;
            var prev = GUI.color;
            GUI.color = new Color(0.4f, 0.8f, 1f, 0.9f);
            for (int b = 0; b < bins; b++)
            {
                if (hu.Bins[b] == 0) continue;
                float h = Mathf.Log10(hu.Bins[b] + 1) / logMax * height;
                var bar = new Rect(rect.x + b * barW, rect.y + height - h, Mathf.Max(barW, 1f), h);
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
            }
            GUI.color = prev;
            GUILayout.Label($"HU {hu.HuStart:F0} .. {hu.HuStart + hu.BinCount * hu.BinWidth:F0}  (对数刻度)");
        }

        void DrawAppearance()
        {
            GUILayout.Label("显色模式", _header);
            int modeIdx = GUILayout.Toolbar((int)_colorMode, _colorModeNames);
            if (modeIdx != (int)_colorMode)
            {
                _colorMode = (DicomColorMode)modeIdx;
                if (_dicomController != null) _dicomController.SetColorMode(_colorMode);
            }

            if (_colorMode == DicomColorMode.Lut && _dicomController != null && _dicomController.LutProfile != null)
            {
                var profile = _dicomController.LutProfile;
                GUILayout.Label($"LUT 预设: {profile.Preset}");
                int presetIdx = GUILayout.Toolbar((int)profile.Preset, _lutPresetNames);
                if (presetIdx != (int)profile.Preset)
                {
                    profile.SetPreset((DicomLutProfile.LutPreset)presetIdx);
                    _dicomController.SetLutProfile(profile);
                }
            }

            if (_colorMode == DicomColorMode.Breakpoint && _dicomController != null)
            {
                var bp = _dicomController.BreakpointProfile;
                if (bp == null || bp.Count == 0)
                {
                    GUILayout.Label("未绑定断点配置或无断点");
                }
                else
                {
                    GUILayout.Label($"断点 ({bp.Count}) 值域 [{bp.DomainMin:F0}, {bp.DomainMax:F0}]");
                    for (int i = 0; i < bp.Count; i++)
                    {
                        var s = bp.Stops[i];
                        var prev = GUI.color;
                        GUI.color = s.Color;
                        GUILayout.Label($"  ■ {s.Value:F0}");
                        GUI.color = prev;
                    }
                }
            }

            if (_dicomPointCloud != null)
            {
                GUILayout.Label($"点大小: {_pointSize:F4}");
                float ps = GUILayout.HorizontalSlider(_pointSize, 0.0001f, 0.02f);
                if (!Mathf.Approximately(ps, _pointSize))
                {
                    _pointSize = ps;
                    _dicomPointCloud.SetPointSize(ps);
                }
            }

            GUILayout.Label($"窗位 Center: {_windowCenter:F2}");
            float wc = GUILayout.HorizontalSlider(_windowCenter, 0f, 1f);
            GUILayout.Label($"窗宽 Width: {_windowWidth:F2}");
            float ww = GUILayout.HorizontalSlider(_windowWidth, 0.01f, 1f);
            if (!Mathf.Approximately(wc, _windowCenter) || !Mathf.Approximately(ww, _windowWidth))
            {
                _windowCenter = wc;
                _windowWidth = ww;
                if (_windowLevel != null) _windowLevel.SetWindow(wc, ww);
            }

            GUILayout.Label($"强度增益: {_gain:F2}");
            float gain = GUILayout.HorizontalSlider(_gain, 0.1f, 4f);
            GUILayout.Label("色调 RGB");
            float tr = GUILayout.HorizontalSlider(_tintR, 0f, 1f);
            float tg = GUILayout.HorizontalSlider(_tintG, 0f, 1f);
            float tb = GUILayout.HorizontalSlider(_tintB, 0f, 1f);
            if (!Mathf.Approximately(gain, _gain) || !Mathf.Approximately(tr, _tintR) ||
                !Mathf.Approximately(tg, _tintG) || !Mathf.Approximately(tb, _tintB))
            {
                _gain = gain;
                _tintR = tr; _tintG = tg; _tintB = tb;
                ApplyTint();
            }
        }

        void DrawDicomTransform()
        {
            if (_dicomTransform == null) { GUILayout.Label("未绑定 DicomModelTransform"); return; }

            float cur = _dicomTransform.CurrentScale;
            GUILayout.Label($"缩放: {cur:F4}  (适配 {_dicomTransform.FitScale:F4})");
            float s = GUILayout.HorizontalSlider(cur, _dicomTransform.MinScale, _dicomTransform.MaxScale);
            if (!Mathf.Approximately(s, cur))
                _dicomTransform.SetScale(s);

            if (GUILayout.Button("复位位置/大小"))
                _dicomTransform.ResetTransform();
        }

        void DrawRebuild()
        {
            GUILayout.Label($"阈值下限: {_thresholdMin:F0}");
            _thresholdMin = GUILayout.HorizontalSlider(_thresholdMin, -1000f, 3000f);
            GUILayout.Label($"阈值上限: {_thresholdMax:F0}");
            _thresholdMax = GUILayout.HorizontalSlider(_thresholdMax, -1000f, 4000f);

            GUILayout.Label($"归一化下限: {_normalizeMin:F0}");
            _normalizeMin = GUILayout.HorizontalSlider(_normalizeMin, -1000f, 3000f);
            GUILayout.Label($"归一化上限: {_normalizeMax:F0}");
            _normalizeMax = GUILayout.HorizontalSlider(_normalizeMax, -1000f, 4000f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply 阈值") && _dicomController != null)
                _dicomController.SetThreshold(_thresholdMin, _thresholdMax);
            if (GUILayout.Button("Apply 归一化") && _dicomController != null)
                _dicomController.SetNormalize(_normalizeMin, _normalizeMax);
            GUILayout.EndHorizontal();

            if (_dicomController != null)
            {
                GUILayout.Label($"重建方向: {_dicomController.ReconstructAxis} 轴");
                int axisIdx = GUILayout.Toolbar((int)_dicomController.ReconstructAxis, _reconstructAxisNames);
                if (axisIdx != (int)_dicomController.ReconstructAxis)
                    _dicomController.SetReconstructAxis((DicomReconstructAxis)axisIdx);

                if (GUILayout.Button("刷新重建点云"))
                    _dicomController.Rebuild();
            }
        }

        void DrawClipping()
        {
            if (_clipping == null) { GUILayout.Label("未绑定 ClippingPlaneController"); return; }
            bool on = GUILayout.Toggle(_clipEnabled, " 启用裁切平面");
            if (on != _clipEnabled)
            {
                _clipEnabled = on;
                _clipping.SetEnabled(on);
            }
        }

        // ============================================================
        // 基因标签页
        // ============================================================
        void DrawGeneTabs()
        {
            if (Foldout("g_status", "加载状态")) DrawGeneStatus();
            GUILayout.Space(6);
            if (Foldout("g_mode", "模式")) DrawGeneMode();
            GUILayout.Space(6);
            if (_geneMode == 1)
            {
                if (Foldout("g_brush", "空间画笔 (mode2)")) DrawBrush();
                GUILayout.Space(6);
                if (Foldout("g_region", "区域分析结果")) DrawRegion();
                GUILayout.Space(6);
            }
            if (Foldout("g_gene", "基因选择")) DrawGeneSelect();
            GUILayout.Space(6);
            if (Foldout("g_lut", "Colormap 预设", false)) DrawGeneLut();
            GUILayout.Space(6);
            if (Foldout("g_transform", "模型变换", false)) DrawGeneTransform();
        }

        void DrawGeneStatus()
        {
            if (_geneController == null)
            {
                GUILayout.Label("未绑定 GeneColorController");
                return;
            }
            if (!_geneLoadRequested)
            {
                GUILayout.Label("切到本标签已触发加载...");
            }

            var r = _geneController.Report;
            GUILayout.Label($"阶段: {r.PhaseText}");
            if (r.Phase == DicomLoadPhase.Parsing)
            {
                GUILayout.Label($"解析进度: {r.FileRatio * 100f:F0}%");
                DrawProgressBar(r.FileRatio);
            }
            if (r.Phase == DicomLoadPhase.Completed)
            {
                GUILayout.Label($"网格: {r.Width} x {r.Height} x {r.Depth}");
                GUILayout.Label($"渲染点数: {r.PointCount:N0}");
                GUILayout.Label($"加载: {r.LoadSeconds:F2}s  建点: {r.BuildSeconds:F2}s");
            }
            if (r.HasError)
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.6f, 0.1f, 0.1f, 0.95f);
                GUILayout.Box($"错误: {r.ErrorMessage}", _errorBox);
                GUI.backgroundColor = prev;
            }
        }

        // 模式切换:切到 mode1 关画笔并清选区回全量渲染
        void DrawGeneMode()
        {
            int m = GUILayout.Toolbar(_geneMode, _geneModeNames);
            if (m != _geneMode)
            {
                _geneMode = m;
                if (_brush != null) _brush.SetEnabled(_geneMode == 1);
                if (_geneMode == 0 && _geneController != null) _geneController.ClearSelection();
            }
        }

        void DrawBrush()
        {
            if (_brush == null) { GUILayout.Label("未绑定 GeneBrushSelector"); return; }

            bool on = GUILayout.Toggle(_brush.BrushEnabled, " 启用画笔(扳机涂抹)");
            if (on != _brush.BrushEnabled) _brush.SetEnabled(on);

            int bm = GUILayout.Toolbar((int)_brush.Mode, _brushModeNames);
            if (bm != (int)_brush.Mode) _brush.SetMode((GeneBrushSelector.BrushMode)bm);

            GUILayout.Label($"笔刷半径: {_brush.WorldRadius * 100f:F1} cm");
            float r = GUILayout.HorizontalSlider(_brush.WorldRadius, 0.005f, 0.2f);
            if (!Mathf.Approximately(r, _brush.WorldRadius)) _brush.SetWorldRadius(r);

            GUILayout.Label($"已选 cell: {_selectedCount:N0}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("清除选择")) _brush.ClearSelection();
            if (GUILayout.Button(_analyzing ? "分析中..." : "确认分析") && !_analyzing)
                StartAnalyze();
            GUILayout.EndHorizontal();

            if (_analyzing)
                GUILayout.Label($"读取基因: {_analyzeProgress * 100f:F0}%");
            if (!string.IsNullOrEmpty(_analyzeHint))
                GUILayout.Label(_analyzeHint);
        }

        void DrawRegion()
        {
            if (_regionReport == null) { GUILayout.Label("确认分析后显示"); return; }
            GUILayout.Label($"区域: {_regionReport.RegionName}  (tag {_regionReport.DominantTag}, {_regionReport.CellCount:N0} cell)");
            GUILayout.Label($"前 {_regionReport.TopGenes.Count} 强表达基因:");
            for (int i = 0; i < _regionReport.TopGenes.Count; i++)
            {
                var g = _regionReport.TopGenes[i];
                if (GUILayout.Button($"{i + 1}. {g.Gene}   均值 {g.MeanExpression:F3}"))
                {
                    // 关画笔清 overlay(选区掩码保留),使区域表达显色可见
                    if (_brush != null) _brush.SetEnabled(false);
                    _geneController.SelectGene(g.Gene);
                }
            }
        }

        void DrawGeneSelect()
        {
            if (_geneController == null || _geneController.Model == null)
            {
                GUILayout.Label("加载完成后可选");
                return;
            }
            if (_genes == null || _genes.Length == 0)
            {
                GUILayout.Label("expression 目录无基因文件");
                return;
            }

            GUILayout.Label($"当前: {_geneController.CurrentGeneName}");
            _geneScroll = GUILayout.BeginScrollView(_geneScroll, GUILayout.Height(160));
            for (int i = 0; i < _genes.Length; i++)
            {
                bool sel = i == _selectedGeneIdx;
                bool now = GUILayout.Toggle(sel, "  " + _genes[i], GUI.skin.button);
                if (now && !sel)
                {
                    _selectedGeneIdx = i;
                    _geneController.SelectGene(_genes[i]);
                }
            }
            GUILayout.EndScrollView();
        }

        void DrawGeneLut()
        {
            if (_geneController == null || _geneController.LutProfile == null) { GUILayout.Label("未绑定 LUT"); return; }
            var profile = _geneController.LutProfile;
            int idx = GUILayout.Toolbar((int)profile.Preset, _lutPresetNames);
            if (idx != (int)profile.Preset)
            {
                profile.SetPreset((DicomLutProfile.LutPreset)idx);
                _geneController.SetLutProfile(profile);
            }
        }

        void DrawGeneTransform()
        {
            if (_geneTransform == null) { GUILayout.Label("未绑定 GeneModelTransform"); return; }
            float cur = _geneTransform.CurrentScale;
            GUILayout.Label($"缩放: {cur:F4}");
            float s = GUILayout.HorizontalSlider(cur, _geneTransform.MinScale, _geneTransform.MaxScale);
            if (!Mathf.Approximately(s, cur)) _geneTransform.SetScale(s);
            if (GUILayout.Button("复位位置/大小")) _geneTransform.ResetTransform();
        }

        // === 基因事件回调 ===
        void OnSelectionChanged(int count) => _selectedCount = count;

        void OnGeneModelLoaded(GeneModelData model)
        {
            // 加载完成后扫 expression 目录列基因(纯 IO,主线程可调)
            _genes = GeneRepository.ListGenes(_geneController.ExpressionDir);
        }

        void OnGeneChanged(string geneName)
        {
            if (_genes == null) return;
            for (int i = 0; i < _genes.Length; i++)
                if (_genes[i] == geneName) { _selectedGeneIdx = i; break; }
        }

        // 确认分析:收集选区->后台读全基因算主导tag+topN->主线程补区域名并刷新
        async void StartAnalyze()
        {
            if (_geneController == null || _geneController.Model == null) return;
            if (!_geneController.CollectSelection(out int[] ids, out int[] tags))
            {
                _analyzeHint = "请先用画笔选择区域";
                return;
            }

            _analyzing = true;
            _analyzeHint = "";
            _bgAnalyzeProgress = 0f;
            _analyzeProgress = 0f;

            try
            {
                var report = await GeneRegionAnalyzer.AnalyzeAsync(
                    ids, tags, _geneController.ExpressionDir, _geneController.Model.CellCount, _topN,
                    p => { _bgAnalyzeProgress = p; _analyzeProgressDirty = true; },
                    System.Threading.CancellationToken.None);

                // 区域名查 ScriptableObject 须主线程,await 后已回主线程
                if (_tagNameTable != null)
                {
                    string name = _tagNameTable.GetName(report.DominantTag);
                    if (!string.IsNullOrEmpty(name)) report.RegionName = name;
                }
                _regionReport = report;
                _analyzeHint = $"分析完成,主导区域 {report.RegionName}";
            }
            catch (System.Exception e)
            {
                _analyzeHint = $"分析失败: {e.Message}";
                Debug.LogError($"区域分析失败: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                _analyzing = false;
            }
        }

        // === 通用 ===
        void DrawProgressBar(float ratio)
        {
            Rect rect = GUILayoutUtility.GetRect(100, 16);
            GUI.Box(rect, GUIContent.none);
            var fill = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(ratio), rect.height);
            var prev = GUI.color;
            GUI.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        void ApplyTint() => Shader.SetGlobalVector(_TintId, new Vector4(_tintR, _tintG, _tintB, _gain));
    }
}
