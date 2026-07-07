using UnityEngine;

using Dicom.Core;

namespace Dicom.Gene
{
    // 基因系统 IMGUI 调试面板:零预制体,桌面即可验证数据->渲染管线
    // 上半显示加载状态,下半选基因(mode1)与切 LUT 预设;F2 切换显隐
    // 世界空间 VR 面板(GenePanelUI + 工厂)为后续项,本面板供开发期快速验证
    public class GeneDebugPanel : MonoBehaviour
    {
        [SerializeField] GeneColorController _controller;
        [SerializeField] GeneModelTransform _modelTransform;
        [SerializeField] GeneBrushSelector _brush;
        // tag->区域名映射,未绑定则回退 "区域{tag}"
        [SerializeField] GeneTagNameTable _tagNameTable;
        [SerializeField] bool _visible = true;
        [SerializeField] KeyCode _toggleKey = KeyCode.F2;
        // top5 取前几强表达基因
        [SerializeField] int _topN = 5;

        // LUT 预设名,顺序须与 DicomLutProfile.LutPreset 一致
        static readonly string[] _lutPresetNames = { "Custom", "热铁", "彩虹", "骨窗", "灰反" };
        static readonly string[] _modeNames = { "mode1 整体", "mode2 区域" };
        static readonly string[] _brushModeNames = { "球形涂抹", "盒框选" };

        string[] _genes;
        int _selectedGeneIdx = -1;
        // 0=mode1 整体, 1=mode2 区域
        int _mode;
        Vector2 _scroll;
        Vector2 _geneScroll;
        GUIStyle _box;
        GUIStyle _header;
        bool _stylesReady;

        // mode2 区域分析状态
        int _selectedCount;
        bool _analyzing;
        float _analyzeProgress;
        volatile bool _analyzeProgressDirty;
        volatile float _bgAnalyzeProgress;
        GeneRegionReport _report;
        string _analyzeHint = "";

        void Start()
        {
            if (_controller == null) _controller = GetComponentInChildren<GeneColorController>();
            if (_modelTransform == null) _modelTransform = GetComponentInChildren<GeneModelTransform>();
            if (_brush == null) _brush = GetComponentInChildren<GeneBrushSelector>();

            if (_controller != null)
            {
                _controller.OnLoaded += OnModelLoaded;
                _controller.OnGeneChanged += OnGeneChanged;
            }
            if (_brush != null) _brush.OnSelectionChanged += OnSelectionChanged;
        }

        void OnDestroy()
        {
            if (_controller != null)
            {
                _controller.OnLoaded -= OnModelLoaded;
                _controller.OnGeneChanged -= OnGeneChanged;
            }
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
        }

        void OnSelectionChanged(int count) => _selectedCount = count;

        void OnModelLoaded(GeneModelData model)
        {
            // 加载完成后扫 expression 目录列基因(纯 IO,主线程可调)
            _genes = GeneRepository.ListGenes(_controller.ExpressionDir);
        }

        void OnGeneChanged(string geneName)
        {
            if (_genes == null) return;
            for (int i = 0; i < _genes.Length; i++)
                if (_genes[i] == geneName) { _selectedGeneIdx = i; break; }
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

        void EnsureStyles()
        {
            if (_stylesReady) return;
            _box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8) };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            float w = 320f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 10, 10, w, Screen.height - 20), _box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            DrawStatusSection();
            GUILayout.Space(8);
            DrawModeSection();
            GUILayout.Space(8);
            if (_mode == 1)
            {
                DrawBrushSection();
                GUILayout.Space(8);
                DrawRegionSection();
                GUILayout.Space(8);
            }
            DrawGeneSection();
            GUILayout.Space(8);
            DrawLutSection();
            GUILayout.Space(8);
            DrawTransformSection();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // 模式切换:切到 mode1 关画笔并清选区回全量渲染
        void DrawModeSection()
        {
            GUILayout.Label("模式", _header);
            int m = GUILayout.Toolbar(_mode, _modeNames);
            if (m != _mode)
            {
                _mode = m;
                if (_brush != null) _brush.SetEnabled(_mode == 1);
                if (_mode == 0 && _controller != null) _controller.ClearSelection();
            }
        }

        // 画笔控件:开关/球盒/半径/清除/确认分析
        void DrawBrushSection()
        {
            if (_brush == null) { GUILayout.Label("未绑定 GeneBrushSelector"); return; }
            GUILayout.Label("空间画笔", _header);

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

        // 区域结果:区域名 + top N 基因列表,点击某基因只渲染该区域该基因
        void DrawRegionSection()
        {
            if (_report == null) return;
            GUILayout.Label("区域分析结果", _header);
            GUILayout.Label($"区域: {_report.RegionName}  (tag {_report.DominantTag}, {_report.CellCount:N0} cell)");
            GUILayout.Label($"前 {_report.TopGenes.Count} 强表达基因:");
            for (int i = 0; i < _report.TopGenes.Count; i++)
            {
                var g = _report.TopGenes[i];
                if (GUILayout.Button($"{i + 1}. {g.Gene}   均值 {g.MeanExpression:F3}"))
                {
                    // 关画笔清 overlay(选区掩码保留),使区域表达显色可见;RebuildPoints 用掩码只渲染选区
                    if (_brush != null) _brush.SetEnabled(false);
                    _controller.SelectGene(g.Gene);
                }
            }
        }

        void DrawStatusSection()
        {
            GUILayout.Label("基因数据加载", _header);
            if (_controller == null)
            {
                GUILayout.Label("未绑定 GeneColorController");
                return;
            }

            var r = _controller.Report;
            GUILayout.Label($"阶段: {r.PhaseText}");
            if (r.Phase == DicomLoadPhase.Parsing)
                GUILayout.Label($"解析进度: {r.FileRatio * 100f:F0}%");
            if (r.Phase == DicomLoadPhase.Completed)
            {
                GUILayout.Label($"网格: {r.Width} x {r.Height} x {r.Depth}");
                GUILayout.Label($"渲染点数: {r.PointCount:N0}");
                GUILayout.Label($"加载: {r.LoadSeconds:F2}s  建点: {r.BuildSeconds:F2}s");
            }
            if (r.HasError)
                GUILayout.Label($"错误: {r.ErrorMessage}");
        }

        // 确认分析:收集选区->后台读全基因算主导tag+topN->主线程补区域名并刷新
        async void StartAnalyze()
        {
            if (_controller == null || _controller.Model == null) return;
            if (!_controller.CollectSelection(out int[] ids, out int[] tags))
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
                    ids, tags, _controller.ExpressionDir, _controller.Model.CellCount, _topN,
                    p => { _bgAnalyzeProgress = p; _analyzeProgressDirty = true; },
                    System.Threading.CancellationToken.None);

                // 区域名查 ScriptableObject 须主线程,await 后已回主线程
                if (_tagNameTable != null)
                {
                    string name = _tagNameTable.GetName(report.DominantTag);
                    if (!string.IsNullOrEmpty(name)) report.RegionName = name;
                }
                _report = report;
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

        void DrawGeneSection()
        {
            GUILayout.Label("基因选择 (mode1)", _header);
            if (_controller == null || _controller.Model == null)
            {
                GUILayout.Label("加载完成后可选");
                return;
            }
            if (_genes == null || _genes.Length == 0)
            {
                GUILayout.Label("expression 目录无基因文件");
                return;
            }

            GUILayout.Label($"当前: {_controller.CurrentGeneName}");
            _geneScroll = GUILayout.BeginScrollView(_geneScroll, GUILayout.Height(160));
            for (int i = 0; i < _genes.Length; i++)
            {
                bool sel = i == _selectedGeneIdx;
                bool now = GUILayout.Toggle(sel, "  " + _genes[i], GUI.skin.button);
                if (now && !sel)
                {
                    _selectedGeneIdx = i;
                    _controller.SelectGene(_genes[i]);
                }
            }
            GUILayout.EndScrollView();
        }

        void DrawLutSection()
        {
            if (_controller == null || _controller.LutProfile == null) return;
            GUILayout.Label("Colormap 预设", _header);
            var profile = _controller.LutProfile;
            int idx = GUILayout.Toolbar((int)profile.Preset, _lutPresetNames);
            if (idx != (int)profile.Preset)
            {
                profile.SetPreset((DicomLutProfile.LutPreset)idx);
                _controller.SetLutProfile(profile);
            }
        }

        void DrawTransformSection()
        {
            if (_modelTransform == null) return;
            GUILayout.Label("模型变换", _header);
            float cur = _modelTransform.CurrentScale;
            GUILayout.Label($"缩放: {cur:F4}");
            float s = GUILayout.HorizontalSlider(cur, _modelTransform.MinScale, _modelTransform.MaxScale);
            if (!Mathf.Approximately(s, cur)) _modelTransform.SetScale(s);
            if (GUILayout.Button("复位位置/大小")) _modelTransform.ResetTransform();
        }
    }
}
