using UnityEngine;

using Dicom.Core;
using Dicom.Analysis;
using Dicom.PointCloud;
using Dicom.Interaction;

namespace Dicom.Demo
{
    // DICOM 调试与参数面板：IMGUI 即时绘制，零预制体依赖
    // 上半部分显示加载诊断(阶段/进度/耗时/尺寸/点数/错误)，下半部分实时调节外观
    // 挂在场景任意物体上，绑定 PointCloudController 即可，F1 切换显隐
    public class DicomDebugPanel : MonoBehaviour
    {
        [SerializeField] PointCloudController _controller;
        [SerializeField] DicomPointCloud _pointCloud;
        [SerializeField] WindowLevelController _windowLevel;
        [SerializeField] ClippingPlaneController _clipping;

        [SerializeField] bool _visible = true;
        [SerializeField] KeyCode _toggleKey = KeyCode.F1;

        // 外观可调值，初始化时从组件读回
        float _pointSize = 0.002f;
        float _windowCenter = 0.5f;
        float _windowWidth = 1f;
        float _tintR = 1f, _tintG = 1f, _tintB = 1f;
        float _gain = 1f;

        // 阈值/归一化走 Apply 防抖，避免拖动中频繁重建点云(README 建议)
        float _thresholdMin = 200f;
        float _thresholdMax = 3000f;
        float _normalizeMin = 200f;
        float _normalizeMax = 1500f;

        bool _clipEnabled = true;

        // 显色模式：灰度强度 / 分类调色板 / 离散 LUT 伪彩，三选一
        DicomColorMode _colorMode = DicomColorMode.Classification;
        static readonly string[] _colorModeNames = { "灰度", "分类", "LUT" };
        // LUT 预设名,顺序须与 DicomLutProfile.LutPreset 枚举一致
        static readonly string[] _lutPresetNames = { "Custom", "热铁", "彩虹", "骨窗", "灰反" };

        // HU 一键应用结果提示
        string _huApplyHint = "";

        Vector2 _scroll;
        GUIStyle _box;
        GUIStyle _header;
        GUIStyle _errorBox;
        bool _stylesReady;

        static readonly int _TintId = Shader.PropertyToID("_DicomTint");

        void Start()
        {
            // 面板未显式绑定时，自动从子物体查找(Demo 动态创建点云的场景)
            if (_controller == null) _controller = GetComponentInChildren<PointCloudController>();
            if (_pointCloud == null) _pointCloud = GetComponentInChildren<DicomPointCloud>();
            if (_windowLevel == null) _windowLevel = GetComponentInChildren<WindowLevelController>();
            if (_clipping == null) _clipping = GetComponentInChildren<ClippingPlaneController>();

            SyncFromComponents();
            ApplyTint();
            // 初始化显色模式,否则 shader 全局变量默认 0 始终走灰度分支,profile 颜色不生效
            if (_controller != null) _controller.SetColorMode(_colorMode);
        }

        // 把当前组件参数读回面板，避免面板默认值覆盖 Inspector 配置
        void SyncFromComponents()
        {
            if (_controller != null)
            {
                _thresholdMin = _controller.ThresholdMin;
                _thresholdMax = _controller.ThresholdMax;
                _normalizeMin = _controller.NormalizeMin;
                _normalizeMax = _controller.NormalizeMax;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(_toggleKey)) _visible = !_visible;
        }

        void EnsureStyles()
        {
            if (_stylesReady) return;
            _box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8) };
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            _errorBox = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8), normal = { textColor = Color.white } };
            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            float w = 360f;
            GUILayout.BeginArea(new Rect(10, 10, w, Screen.height - 20), _box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            DrawStatusSection();
            GUILayout.Space(8);
            DrawHuRangeSection();
            GUILayout.Space(8);
            DrawAppearanceSection();
            GUILayout.Space(8);
            DrawRebuildSection();
            GUILayout.Space(8);
            DrawClippingSection();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // 加载诊断：阶段、进度条、当前文件、体数据尺寸、点数、耗时；失败时红框显示错误
        void DrawStatusSection()
        {
            GUILayout.Label("DICOM 加载状态", _header);

            if (_controller == null)
            {
                GUILayout.Box("未绑定 PointCloudController", _errorBox);
                return;
            }

            var r = _controller.Report;

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

            // 解析阶段显示文件进度条
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

        // HU 区间分析:对数刻度直方图 + 自动识别的占用区间列表 + 一键写入分类配置
        void DrawHuRangeSection()
        {
            GUILayout.Label("HU 区间分析", _header);

            if (_controller == null) return;
            var hu = _controller.HuReport;
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
                bool ok = _controller.ApplyDetectedRangesToProfile();
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

        // 外观调节：实时生效，不重建点云
        void DrawAppearanceSection()
        {
            GUILayout.Label("外观 (实时)", _header);

            // 显色模式三选一:灰度强度 / 分类调色板 / 离散 LUT 伪彩
            GUILayout.Label("显色模式", _header);
            int modeIdx = GUILayout.Toolbar((int)_colorMode, _colorModeNames);
            if (modeIdx != (int)_colorMode)
            {
                _colorMode = (DicomColorMode)modeIdx;
                if (_controller != null) _controller.SetColorMode(_colorMode);
            }

            // LUT 模式下显示预设选择,切换即重新烘焙上传
            if (_colorMode == DicomColorMode.Lut && _controller != null && _controller.LutProfile != null)
            {
                var profile = _controller.LutProfile;
                GUILayout.Label($"LUT 预设: {profile.Preset}");
                int presetIdx = GUILayout.Toolbar((int)profile.Preset, _lutPresetNames);
                if (presetIdx != (int)profile.Preset)
                {
                    profile.SetPreset((DicomLutProfile.LutPreset)presetIdx);
                    _controller.SetLutProfile(profile);
                }
            }

            if (_pointCloud != null)
            {
                GUILayout.Label($"点大小: {_pointSize:F4}");
                float ps = GUILayout.HorizontalSlider(_pointSize, 0.0001f, 0.02f);
                if (!Mathf.Approximately(ps, _pointSize))
                {
                    _pointSize = ps;
                    _pointCloud.SetPointSize(ps);
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

        // 阈值/归一化：拖动只改面板值，点 Apply 才重建点云(防抖)
        void DrawRebuildSection()
        {
            GUILayout.Label("点生成 (需 Apply)", _header);

            GUILayout.Label($"阈值下限: {_thresholdMin:F0}");
            _thresholdMin = GUILayout.HorizontalSlider(_thresholdMin, -1000f, 3000f);
            GUILayout.Label($"阈值上限: {_thresholdMax:F0}");
            _thresholdMax = GUILayout.HorizontalSlider(_thresholdMax, -1000f, 4000f);

            GUILayout.Label($"归一化下限: {_normalizeMin:F0}");
            _normalizeMin = GUILayout.HorizontalSlider(_normalizeMin, -1000f, 3000f);
            GUILayout.Label($"归一化上限: {_normalizeMax:F0}");
            _normalizeMax = GUILayout.HorizontalSlider(_normalizeMax, -1000f, 4000f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply 阈值") && _controller != null)
                _controller.SetThreshold(_thresholdMin, _thresholdMax);
            if (GUILayout.Button("Apply 归一化") && _controller != null)
                _controller.SetNormalize(_normalizeMin, _normalizeMax);
            GUILayout.EndHorizontal();
        }

        void DrawClippingSection()
        {
            if (_clipping == null) return;
            GUILayout.Label("裁切", _header);
            bool on = GUILayout.Toggle(_clipEnabled, " 启用裁切平面");
            if (on != _clipEnabled)
            {
                _clipEnabled = on;
                _clipping.SetEnabled(on);
            }
        }

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
