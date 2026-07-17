using System;
using System.Collections.Generic;
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

        // 未画取区域淡显不透明度(0=全透明,1=不透明);由 Bootstrap 注入,面板可实时调;现驱动灰色幽灵底图
        [SerializeField, Range(0f, 1f)] float _selectionFade = 0.12f;

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

        public GeneModelData Model => _model;
        public GeneExpression CurrentGene => _currentGene;
        public string CurrentGeneName => _currentGene != null ? _currentGene.GeneName : "";
        public DicomLoadReport Report => _report;
        public Bounds LocalBounds => _localBounds;
        public DicomLutProfile LutProfile => _lutProfile;
        public bool HasSelection => _mask.IsCreated;

        // 模型全量 cell 的 local 尺寸(mm,已居中,与缩放无关);供信标按模型体量定尺寸
        public Vector3 ModelLocalSize
        {
            get
            {
                if (_model == null) return Vector3.zero;
                float3 span = ((float3)(_model.GridMax - _model.GridMin)) * _cellSpacing;
                return (Vector3)span;
            }
        }

        // 未画取区域淡显不透明度(现由灰色幽灵底图承载,GeneBrushVisual 读此值设幽灵 alpha);
        // 主点云只含已画取 cell 恒不透明,不再受此值影响
        public event Action<float> OnSelectionFadeChanged;
        public float SelectionFade
        {
            get => _selectionFade;
            set
            {
                _selectionFade = Mathf.Clamp01(value);
                OnSelectionFadeChanged?.Invoke(_selectionFade);
            }
        }
        // 加载完成后可用,供面板列基因菜单
        public string ExpressionDir => _exprDir;

        void Awake()
        {
            _pointCloud = GetComponent<DicomPointCloud>();
            // LUT profile 可能是多系统共享的资产:实例化运行时副本各自持有,
            // 使烘焙纹理/预设切换只作用于本实例,不销毁共享资产纹理也不写回污染资产
            if (_lutProfile != null) _lutProfile = Instantiate(_lutProfile);
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
                // 旧模型的选中掩码/当前基因长度绑定旧 CellCount,切换到不同尺寸数据集后
                // 复用会导致 Burst Job 按新 CellCount 遍历时越界(Player 上硬崩溃),这里一并失效
                if (_mask.IsCreated) _mask.Dispose();
                _currentGene = null;
                _geneRequestSeq = 0;
                _model = model;
                BuildNativeCells(model);
                BuildRegionRoster();

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

            int cellCount = _model.CellCount;
            // 基因值数组与掩码长度必须匹配当前 CellCount,否则 Burst Job 越界读/写。
            // 正常流程由 Load 失效+EnsureMask 保证,这里快速失败暴露装配错误而非静默越界
            if (_currentGene.Values.Length != cellCount)
            {
                Debug.LogError($"基因值长度 {_currentGene.Values.Length} 与模型 CellCount {cellCount} 不匹配,跳过重建");
                return;
            }
            if (_mask.IsCreated && _mask.Length != cellCount)
            {
                Debug.LogError($"选中掩码长度 {_mask.Length} 与模型 CellCount {cellCount} 不匹配,跳过重建");
                return;
            }

            var buildTimer = Stopwatch.StartNew();

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
                // 主点云只含已画取(或无掩码全量)cell,恒不透明;未画取区域由灰色幽灵底图呈现
                _pointCloud.SetAlpha(1f);

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
            if (_model == null) return _mask;
            // 掩码长度必须与当前模型 CellCount 一致,残留的旧长度掩码先释放再重建,防画笔 Job 越界
            if (_mask.IsCreated && _mask.Length != _model.CellCount) _mask.Dispose();
            if (!_mask.IsCreated)
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

        // 区域汇总:选中掩码内各 tag 的置位数(降序)+ 选中 cell 的 local 质心
        // 供空间文本列出"区域内全部 tag(主导优先)",指向线连到质心。缓冲区复用防 GC
        // tag 数很少(数十),字典计数 O(CellCount) 单遍;仅选中集变化时调用,非每帧
        readonly Dictionary<int, int> _tagCountBuf = new Dictionary<int, int>();
        readonly List<TagShare> _tagShareBuf = new List<TagShare>();
        static readonly Comparison<TagShare> _tagShareDesc = (a, b) => b.Count.CompareTo(a.Count);

        public struct TagShare { public int Tag; public int Count; }

        // 返回按占比降序的 tag 列表(引用内部复用缓冲,勿缓存)与 local 质心;无选中返回 false
        public bool CollectRegionSummary(out List<TagShare> shares, out Vector3 localCentroid, out int total)
        {
            shares = _tagShareBuf;
            shares.Clear();
            _tagCountBuf.Clear();
            localCentroid = Vector3.zero;
            total = 0;
            if (!_mask.IsCreated || _model == null || !_model.NativeReady) return false;

            float3 sum = float3.zero;
            for (int i = 0; i < _mask.Length; i++)
            {
                if (_mask[i] == 0) continue;
                int t = _model.CellTag[i];
                _tagCountBuf.TryGetValue(t, out int c);
                _tagCountBuf[t] = c + 1;
                sum += _model.CellPos[i];
                total++;
            }
            if (total == 0) return false;

            localCentroid = (Vector3)(sum / total);
            foreach (var kv in _tagCountBuf)
                shares.Add(new TagShare { Tag = kv.Key, Count = kv.Value });
            shares.Sort(_tagShareDesc);
            return true;
        }

        // === 区域花名册(全量,加载后一次性统计,供覆盖率信标) ===
        // 每个 tag 的总 cell 数 + local 质心;不随选区变化,模型加载后即固定
        public struct RegionInfo { public int Tag; public int Total; public Vector3 LocalCentroid; }

        readonly List<RegionInfo> _regionRoster = new List<RegionInfo>();

        // 全部区域花名册(引用内部复用列表,勿缓存长期持有);模型未就绪为空
        public IReadOnlyList<RegionInfo> RegionRoster => _regionRoster;

        // 遍历全量 cell 按 tag 汇总总数与质心;O(CellCount) 单遍,仅加载时调一次
        void BuildRegionRoster()
        {
            _regionRoster.Clear();
            if (_model == null || !_model.NativeReady) return;

            var count = new Dictionary<int, int>();
            var sum = new Dictionary<int, float3>();
            for (int i = 0; i < _model.CellCount; i++)
            {
                int t = _model.CellTag[i];
                count.TryGetValue(t, out int c);
                count[t] = c + 1;
                sum.TryGetValue(t, out float3 s);
                sum[t] = s + _model.CellPos[i];
            }

            foreach (var kv in count)
            {
                int total = kv.Value;
                _regionRoster.Add(new RegionInfo
                {
                    Tag = kv.Key,
                    Total = total,
                    LocalCentroid = (Vector3)(sum[kv.Key] / total)
                });
            }
        }

        // 收集当前掩码内各 tag 的已画 cell 数(写入调用方传入的字典,已清空);无掩码则全零(不写入)
        public void CollectPaintedByTag(Dictionary<int, int> painted)
        {
            painted.Clear();
            if (!_mask.IsCreated || _model == null || !_model.NativeReady) return;
            for (int i = 0; i < _mask.Length; i++)
            {
                if (_mask[i] == 0) continue;
                int t = _model.CellTag[i];
                painted.TryGetValue(t, out int c);
                painted[t] = c + 1;
            }
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

        // 把全模型全部 cell 构建成幽灵点集写入指定 DicomPointCloud(恒定强度,全 Selected=0)
        // 供 GeneBrushVisual 完整底图:全模型灰白半透明常驻(不写深度不遮挡),已画取的不透明彩色点
        // 由主点云/overlay 叠在上层覆盖。与掩码/基因无关,模型加载后建一次即可,不随选区重建
        // bounds 用全模型(点遍布全模型)
        public void BuildGhost(DicomPointCloud ghost, float intensity)
        {
            if (ghost == null || _model == null || !_model.NativeReady)
            {
                if (ghost != null) ghost.SetPoints(default, 0);
                return;
            }

            int cellCount = _model.CellCount;
            if (cellCount <= 0)
            {
                ghost.SetPoints(default, 0);
                return;
            }

            const int blockSize = 4096;
            int blocks = (cellCount + blockSize - 1) / blockSize;

            // 渲染全部 cell:输出下标==cell 下标,无需计数/前缀和,单遍直接写入
            var points = new NativeArray<DicomPoint>(cellCount, Allocator.TempJob);
            try
            {
                new GeneGhostWriteJob
                {
                    CellPos = _model.CellPos,
                    BlockSize = blockSize,
                    CellCount = cellCount,
                    Intensity = intensity,
                    Points = points
                }.Schedule(blocks, 1).Complete();

                ghost.SetPoints(points, cellCount);
                ghost.SetLocalBounds(new Bounds(Vector3.zero, ModelLocalSize));
            }
            finally
            {
                if (points.IsCreated) points.Dispose();
            }
        }

        // === 显色 ===
        // 全部写入本点云实例 property block,与 DICOM HU 点云的显色态互不干扰(不走 Shader 全局)
        void ApplyLut()
        {
            if (_lutProfile == null) return;
            _pointCloud.SetLutTexture(_lutProfile.BakeLut());
            _pointCloud.SetColorMode((float)DicomColorMode.Lut);
        }

        // 运行时更换 LUT,重新烘焙上传。传入资产则实例化副本持有,并回收旧副本纹理
        public void SetLutProfile(DicomLutProfile profile)
        {
            if (_lutProfile != null) _lutProfile.DestroyBaked();
            _lutProfile = profile != null ? Instantiate(profile) : null;
            ApplyLut();
        }

        void ApplyNormalize(float min, float max)
        {
            _pointCloud.SetNormalize(min, max);
        }

        // 初始化本点云实例显色态:LUT 模式 + 窗宽窗位全通 + 白色调
        // 显色态挂在实例 property block 上,不再是 Shader 全局,天然无跨 PlayMode 残留问题
        void ResetShaderGlobals()
        {
            _pointCloud.SetColorMode((float)DicomColorMode.Lut);
            _pointCloud.SetWindow(0.5f, 1f);
            _pointCloud.SetTint(1f, 1f, 1f, 1f);
        }

        // 把本点云当前显色态复制到 overlay 点云,使高亮点用与主点云一致的 colormap
        public void ApplyColorState(DicomPointCloud target)
        {
            _pointCloud.CopyColorStateTo(target);
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
            // _lutProfile 是 Awake/SetLutProfile 实例化的运行时副本,连同烘焙纹理一并销毁
            if (_lutProfile != null)
            {
                _lutProfile.DestroyBaked();
                Destroy(_lutProfile);
            }
        }
    }
}
