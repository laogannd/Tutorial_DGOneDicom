using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

using Dicom.Core;
using Dicom.PointCloud;

namespace Dicom.Gene
{
    // 基因表达显色协调器:后台加载 cell_mapping -> 主线程建常驻 NativeArray -> 选基因 -> Burst 构建点集 -> LUT 显色
    // 复用 DicomPointCloud 渲染与 shader LUT colormap;基因/HU 不同屏,显色走 shader 全局变量
    // 加载/进度模式仿 PointCloudController(volatile 标志 + 主线程 Update 派发)
    [RequireComponent(typeof(DicomPointCloud))]
    public class GeneColorController : MonoBehaviour
    {
        // cell 网格每格对应的 local 尺寸(mm),整体缩放由 GeneModelTransform 适配到米级
        [SerializeField] float _cellSpacing = 1f;

        // 基因表达 colormap 配置,未绑定则显色落回灰度
        [SerializeField] DicomLutProfile _lutProfile;

        // 加载完成(携带 local 尺寸信息)
        public event Action<GeneModelData> OnLoaded;
        // 每次重建点集后触发,携带可见点局部 AABB,供碰撞盒/线框紧贴
        public event Action<Bounds> OnBoundsChanged;
        public event Action<Exception> OnError;
        // 阶段/进度变化,面板据此刷新
        public event Action<DicomLoadReport> OnReportChanged;
        // 当前基因切换完成,携带基因名
        public event Action<string> OnGeneChanged;

        DicomPointCloud _pointCloud;
        GeneModelData _model;
        GeneExpression _currentGene;
        string _exprDir;

        // 选中掩码,长度 CellCount;mode2 只渲染置位 cell。null/未创建表示 mode1 全选
        NativeArray<byte> _mask;

        Bounds _localBounds = new Bounds(Vector3.zero, Vector3.zero);
        CancellationTokenSource _cts;
        // 切基因请求序号,防旧任务覆盖新结果
        int _geneRequestSeq;

        // 后台线程只写这些 volatile 标志,主线程 Update 合并到 _report
        volatile DicomLoadPhase _bgPhase;
        volatile float _bgProgress;
        volatile bool _progressDirty;

        readonly DicomLoadReport _report = new DicomLoadReport();
        readonly Stopwatch _loadTimer = new Stopwatch();

        static readonly int _ColorModeId = Shader.PropertyToID("_DicomColorMode");
        static readonly int _LutTexId = Shader.PropertyToID("_DicomLut");
        static readonly int _NormalizeId = Shader.PropertyToID("_DicomNormalize");
        static readonly int _TintId = Shader.PropertyToID("_DicomTint");
        static readonly int _WindowId = Shader.PropertyToID("_DicomWindow");

        public GeneModelData Model => _model;
        public GeneExpression CurrentGene => _currentGene;
        public string CurrentGeneName => _currentGene != null ? _currentGene.GeneName : "";
        public DicomLoadReport Report => _report;
        public Bounds LocalBounds => _localBounds;
        public DicomLutProfile LutProfile => _lutProfile;
        public bool HasSelection => _mask.IsCreated;
        // 加载完成后可用,供面板列基因菜单
        public string ExpressionDir => _exprDir;

        void Awake()
        {
            _pointCloud = GetComponent<DicomPointCloud>();
            // 显式初始化 shader 全局显色态,项目禁用 Domain Reload 防跨 PlayMode 残留
            ResetShaderGlobals();
            ApplyLut();
        }

        // 从目录异步加载:目录内含 cell_mapping.json 与 expression/ 子目录
        public async void Load(string directory)
        {
            CancelOngoing();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _report.Reset();
            _report.Phase = DicomLoadPhase.Scanning;
            _bgPhase = DicomLoadPhase.Scanning;
            RaiseReport();
            _loadTimer.Restart();

            _exprDir = System.IO.Path.Combine(directory, GeneRepository.ExpressionDirName);
            string mappingPath = System.IO.Path.Combine(directory, GeneRepository.CellMappingFileName);

            try
            {
                _bgPhase = DicomLoadPhase.Parsing;
                var model = await GeneRepository.LoadCellMappingAsync(mappingPath, p =>
                {
                    _bgProgress = p;
                    _progressDirty = true;
                }, token);

                if (token.IsCancellationRequested) return;

                _loadTimer.Stop();
                _report.LoadSeconds = (float)_loadTimer.Elapsed.TotalSeconds;

                // 释放旧模型常驻数组,建新模型的常驻 CellPos/CellTag(主线程)
                DisposeModel();
                _model = model;
                BuildNativeCells(model);

                _report.Width = model.GridMax.x - model.GridMin.x + 1;
                _report.Height = model.GridMax.y - model.GridMin.y + 1;
                _report.Depth = model.GridMax.z - model.GridMin.z + 1;
                _report.Phase = DicomLoadPhase.Completed;
                RaiseReport();
                OnLoaded?.Invoke(model);
            }
            catch (OperationCanceledException)
            {
                // 主动取消,忽略
            }
            catch (Exception e)
            {
                _loadTimer.Stop();
                _report.Phase = DicomLoadPhase.Failed;
                _report.ErrorMessage = e.Message;
                _report.ErrorStack = e.StackTrace ?? "";
                RaiseReport();
                Debug.LogError($"基因数据加载失败: {e.Message}\n{e.StackTrace}");
                OnError?.Invoke(e);
            }
        }

        void Update()
        {
            if (_progressDirty)
            {
                _progressDirty = false;
                _report.Phase = _bgPhase;
                OnProgress(_bgProgress);
                RaiseReport();
            }
        }

        void OnProgress(float ratio)
        {
            // 复用 report 的文件比例字段展示解析进度(0..1 映射为 done/total 千分比)
            _report.FilesTotal = 1000;
            _report.FilesDone = Mathf.RoundToInt(Mathf.Clamp01(ratio) * 1000f);
        }

        // 把网格坐标居中为 local 坐标(mm),建常驻 NativeArray;全生命周期复用不重分配
        void BuildNativeCells(GeneModelData model)
        {
            float3 center = ((float3)(model.GridMin + model.GridMax)) * 0.5f;

            var pos = new NativeArray<float3>(model.CellCount, Allocator.Persistent);
            var tag = new NativeArray<int>(model.CellCount, Allocator.Persistent);
            for (int i = 0; i < model.CellCount; i++)
            {
                pos[i] = ((float3)model.Grid[i] - center) * _cellSpacing;
                tag[i] = model.Tag[i];
            }
            model.CellPos = pos;
            model.CellTag = tag;
        }

        // 切当前基因:后台解析表达文件,完成后主线程重建点集。防竞态:请求序号校验
        public async void SelectGene(string geneName)
        {
            if (_model == null) { Debug.LogWarning("模型未加载,无法选基因"); return; }
            if (string.IsNullOrEmpty(geneName)) return;

            int seq = ++_geneRequestSeq;
            var token = _cts != null ? _cts.Token : CancellationToken.None;

            try
            {
                var gene = await GeneRepository.LoadGeneAsync(_exprDir, geneName, _model.CellCount, token);
                // 期间又切了别的基因或已取消,丢弃过期结果
                if (seq != _geneRequestSeq || token.IsCancellationRequested) return;

                _currentGene = gene;
                RebuildPoints();
                OnGeneChanged?.Invoke(gene.GeneName);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogError($"基因表达加载失败 [{geneName}]: {e.Message}");
                OnError?.Invoke(e);
            }
        }

        // 用当前基因 + 当前掩码重建点集;无基因时静默忽略
        public void RebuildPoints()
        {
            if (_model == null || _currentGene == null) return;
            if (!_model.NativeReady) return;

            var buildTimer = Stopwatch.StartNew();

            int cellCount = _model.CellCount;
            const int blockSize = 4096;
            int blocks = (cellCount + blockSize - 1) / blockSize;

            var values = new NativeArray<float>(_currentGene.Values, Allocator.TempJob);
            var blockCounts = new NativeArray<int>(blocks, Allocator.TempJob);
            var blockOffsets = new NativeArray<int>(blocks, Allocator.TempJob);
            NativeArray<DicomPoint> points = default;
            NativeArray<float3> blockMin = default;
            NativeArray<float3> blockMax = default;
            // 无选择时传零长掩码(全选);GeneWriteJob 按 Mask.Length==0 判断
            NativeArray<byte> emptyMask = default;
            NativeArray<byte> maskArg = _mask.IsCreated ? _mask : (emptyMask = new NativeArray<byte>(0, Allocator.TempJob));

            try
            {
                var countJob = new GeneCountJob
                {
                    Values = values,
                    Mask = maskArg,
                    BlockSize = blockSize,
                    CellCount = cellCount,
                    BlockCounts = blockCounts
                };
                countJob.Schedule(blocks, 1).Complete();

                int total = 0;
                for (int b = 0; b < blocks; b++)
                {
                    blockOffsets[b] = total;
                    total += blockCounts[b];
                }

                if (total <= 0)
                {
                    _pointCloud.SetPoints(default, 0);
                    _localBounds = new Bounds(Vector3.zero, Vector3.zero);
                    _pointCloud.SetLocalBounds(_localBounds);
                    _report.PointCount = 0;
                    RaiseReport();
                    OnBoundsChanged?.Invoke(_localBounds);
                    Debug.LogWarning("当前基因/选区无可见 cell");
                    return;
                }

                points = new NativeArray<DicomPoint>(total, Allocator.TempJob);
                blockMin = new NativeArray<float3>(blocks, Allocator.TempJob);
                blockMax = new NativeArray<float3>(blocks, Allocator.TempJob);

                var writeJob = new GeneWriteJob
                {
                    CellPos = _model.CellPos,
                    Values = values,
                    CellTag = _model.CellTag,
                    Mask = maskArg,
                    BlockOffsets = blockOffsets,
                    BlockSize = blockSize,
                    CellCount = cellCount,
                    NormalizeMin = _currentGene.Min,
                    NormalizeMax = _currentGene.Max,
                    Points = points,
                    BlockMin = blockMin,
                    BlockMax = blockMax
                };
                writeJob.Schedule(blocks, 1).Complete();

                _pointCloud.SetPoints(points, total);

                float3 lo = new float3(float.MaxValue);
                float3 hi = new float3(float.MinValue);
                for (int b = 0; b < blocks; b++)
                {
                    if (blockMin[b].x > blockMax[b].x) continue;
                    lo = math.min(lo, blockMin[b]);
                    hi = math.max(hi, blockMax[b]);
                }

                Vector3 c = (Vector3)((lo + hi) * 0.5f);
                Vector3 size = (Vector3)(hi - lo);
                float minThickness = _cellSpacing;
                size.x = Mathf.Max(size.x, minThickness);
                size.y = Mathf.Max(size.y, minThickness);
                size.z = Mathf.Max(size.z, minThickness);
                _localBounds = new Bounds(c, size);
                _pointCloud.SetLocalBounds(_localBounds);

                // 归一化范围上传 shader,供断点模式反推(此处 LUT 模式亦无害),窗宽窗位复位为全通
                ApplyNormalize(_currentGene.Min, _currentGene.Max);

                buildTimer.Stop();
                _report.PointCount = total;
                _report.BuildSeconds = (float)buildTimer.Elapsed.TotalSeconds;
                RaiseReport();
                OnBoundsChanged?.Invoke(_localBounds);
            }
            finally
            {
                if (values.IsCreated) values.Dispose();
                if (blockCounts.IsCreated) blockCounts.Dispose();
                if (blockOffsets.IsCreated) blockOffsets.Dispose();
                if (points.IsCreated) points.Dispose();
                if (blockMin.IsCreated) blockMin.Dispose();
                if (blockMax.IsCreated) blockMax.Dispose();
                if (emptyMask.IsCreated) emptyMask.Dispose();
            }
        }

        // === 选中掩码(mode2) ===
        // 获取常驻掩码,不存在则建全零;供 GeneBrushSelector 直接写入
        public NativeArray<byte> EnsureMask()
        {
            if (!_mask.IsCreated && _model != null)
                _mask = new NativeArray<byte>(_model.CellCount, Allocator.Persistent);
            return _mask;
        }

        // 清空选择:掩码全零并回到全量渲染
        public void ClearSelection()
        {
            if (_mask.IsCreated) _mask.Dispose();
            RebuildPoints();
        }

        // 从掩码收集选中 cellId 与对应 tag(主线程),供后台区域分析;无选中返回 false
        public bool CollectSelection(out int[] ids, out int[] tags)
        {
            ids = null;
            tags = null;
            if (!_mask.IsCreated || _model == null) return false;

            int count = 0;
            for (int i = 0; i < _mask.Length; i++)
                if (_mask[i] != 0) count++;
            if (count == 0) return false;

            ids = new int[count];
            tags = new int[count];
            int w = 0;
            for (int i = 0; i < _mask.Length; i++)
            {
                if (_mask[i] == 0) continue;
                ids[w] = i;
                tags[w] = _model.CellTag[i];
                w++;
            }
            return true;
        }

        // 是否处于区域模式(有选中掩码且非全零由调用方保证)
        public void ApplySelection() => RebuildPoints();

        // 把当前选中掩码构建成 overlay 点集写入指定 DicomPointCloud(恒定强度高亮)
        // 供 GeneBrushVisual 高亮选区;无选中则清空 overlay
        public void BuildOverlay(DicomPointCloud overlay, float intensity)
        {
            if (overlay == null || _model == null || !_model.NativeReady || !_mask.IsCreated)
            {
                if (overlay != null) overlay.SetPoints(default, 0);
                return;
            }

            int cellCount = _model.CellCount;
            const int blockSize = 4096;
            int blocks = (cellCount + blockSize - 1) / blockSize;

            var blockCounts = new NativeArray<int>(blocks, Allocator.TempJob);
            var blockOffsets = new NativeArray<int>(blocks, Allocator.TempJob);
            NativeArray<DicomPoint> points = default;

            try
            {
                new GeneOverlayCountJob
                {
                    Mask = _mask,
                    BlockSize = blockSize,
                    CellCount = cellCount,
                    BlockCounts = blockCounts
                }.Schedule(blocks, 1).Complete();

                int total = 0;
                for (int b = 0; b < blocks; b++)
                {
                    blockOffsets[b] = total;
                    total += blockCounts[b];
                }

                if (total <= 0)
                {
                    overlay.SetPoints(default, 0);
                    return;
                }

                points = new NativeArray<DicomPoint>(total, Allocator.TempJob);
                new GeneOverlayWriteJob
                {
                    CellPos = _model.CellPos,
                    Mask = _mask,
                    BlockOffsets = blockOffsets,
                    BlockSize = blockSize,
                    CellCount = cellCount,
                    Intensity = intensity,
                    Points = points
                }.Schedule(blocks, 1).Complete();

                overlay.SetPoints(points, total);
                overlay.SetLocalBounds(_localBounds);
            }
            finally
            {
                if (blockCounts.IsCreated) blockCounts.Dispose();
                if (blockOffsets.IsCreated) blockOffsets.Dispose();
                if (points.IsCreated) points.Dispose();
            }
        }

        // === 显色 ===
        void ApplyLut()
        {
            if (_lutProfile == null) return;
            Shader.SetGlobalTexture(_LutTexId, _lutProfile.BakeLut());
            Shader.SetGlobalFloat(_ColorModeId, (float)DicomColorMode.Lut);
        }

        // 运行时更换 LUT,重新烘焙上传
        public void SetLutProfile(DicomLutProfile profile)
        {
            _lutProfile = profile;
            ApplyLut();
        }

        void ApplyNormalize(float min, float max)
        {
            Shader.SetGlobalVector(_NormalizeId, new Vector4(min, max, 0f, 0f));
        }

        // 复位 shader 全局显色态:LUT 模式 + 窗宽窗位全通 + 白色调,消除跨 PlayMode 残留
        void ResetShaderGlobals()
        {
            Shader.SetGlobalFloat(_ColorModeId, (float)DicomColorMode.Lut);
            Shader.SetGlobalVector(_WindowId, new Vector4(0.5f, 1f, 0f, 0f));
            Shader.SetGlobalVector(_TintId, new Vector4(1f, 1f, 1f, 1f));
        }

        void RaiseReport() => OnReportChanged?.Invoke(_report);

        void CancelOngoing()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        void DisposeModel()
        {
            if (_model != null) _model.DisposeNative();
        }

        void OnDestroy()
        {
            CancelOngoing();
            DisposeModel();
            if (_mask.IsCreated) _mask.Dispose();
            if (_lutProfile != null) _lutProfile.DestroyBaked();
        }
    }
}
