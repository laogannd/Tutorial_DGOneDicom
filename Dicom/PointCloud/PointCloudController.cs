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
using Dicom.Analysis;

namespace Dicom.PointCloud
{
    // 显色模式：灰度强度 / 按 HU 分类调色板 / 离散 LUT 伪彩 / 断点插值，对应 shader _DicomColorMode 0/1/2/3
    public enum DicomColorMode
    {
        Intensity = 0,
        Classification = 1,
        Lut = 2,
        Breakpoint = 3
    }

    // 切片堆叠轴指向的世界轴，决定点云按哪个方向重建；值与 Job 的 ReconstructAxis 一致
    public enum DicomReconstructAxis
    {
        X = 0,
        Y = 1,
        Z = 2
    }

    // 协调完整管线：后台解析 DICOM -> Burst 体素转点 -> 上传 ComputeBuffer -> 渲染
    // 全程维护 DicomLoadReport 诊断快照，供调试面板读取阶段、进度、耗时与错误
    [RequireComponent(typeof(DicomPointCloud))]
    public class PointCloudController : MonoBehaviour
    {        [SerializeField] float _thresholdMin = 200f;
        [SerializeField] float _thresholdMax = 3000f;
        [SerializeField] float _normalizeMin = 200f;
        [SerializeField] float _normalizeMax = 1500f;

        // 重建方向：切片堆叠轴映射到的世界轴，默认 Z(与原行为一致)
        [SerializeField] DicomReconstructAxis _reconstructAxis = DicomReconstructAxis.Z;

        // HU 区间分类配置，未绑定则不分类(所有点 ClassId = -1)
        [SerializeField] DicomClassificationProfile _classificationProfile;

        // 离散 LUT 伪彩配置，未绑定则 LUT 模式不可用(SetColorMode 落回灰度)
        [SerializeField] DicomLutProfile _lutProfile;

        // 断点插值显色配置，未绑定则断点模式不可用
        [SerializeField] DicomBreakpointProfile _breakpointProfile;

        public event Action<float> OnProgress;
        public event Action<DicomDataset> OnLoaded;
        // 每次重建点云后触发,携带阈值过滤后可见点的真实局部 AABB(已按重建方向重排)
        // 调阈值/归一化/重建方向/分类都会重建并刷新,供碰撞盒与线框紧贴实际点云
        public event Action<Bounds> OnBoundsChanged;
        public event Action<Exception> OnError;
        // 阶段或诊断信息变化时触发，调试面板据此刷新
        public event Action<DicomLoadReport> OnReportChanged;
        // HU 区间分析完成时触发,携带直方图与自动识别的占用区间
        public event Action<HuRangeReport> OnHuAnalyzed;

        DicomPointCloud _pointCloud;
        DicomDataset _dataset;
        // 整卷体素的常驻 NativeArray，与 _dataset 绑定复用：调阈值/方向/归一化重建时不再逐次重拷上百 MB
        NativeArray<short> _voxels;
        HuRangeReport _huReport;
        CancellationTokenSource _cts;
        // 最近一次重建得到的可见点局部 AABB,供加载后晚订阅者补取当前值
        Bounds _localBounds = new Bounds(Vector3.zero, Vector3.zero);

        // 后台线程只写这些 volatile 标志，主线程 Update 合并到 _report
        volatile DicomLoadPhase _bgPhase;
        volatile int _progressDone;
        volatile int _progressTotal;
        volatile string _bgCurrentFile = "";
        volatile bool _progressDirty;

        readonly DicomLoadReport _report = new DicomLoadReport();
        readonly Stopwatch _loadTimer = new Stopwatch();

        public DicomDataset Dataset => _dataset;
        public DicomLoadReport Report => _report;
        public HuRangeReport HuReport => _huReport;
        // 最近一次重建的可见点局部 AABB(中心可偏离原点),晚订阅 OnBoundsChanged 的组件可据此补取
        public Bounds LocalBounds => _localBounds;

        public float ThresholdMin => _thresholdMin;
        public float ThresholdMax => _thresholdMax;
        public float NormalizeMin => _normalizeMin;
        public float NormalizeMax => _normalizeMax;
        public DicomReconstructAxis ReconstructAxis => _reconstructAxis;
        public DicomClassificationProfile ClassificationProfile => _classificationProfile;
        public DicomLutProfile LutProfile => _lutProfile;
        public DicomBreakpointProfile BreakpointProfile => _breakpointProfile;

        static readonly int _ColorModeId = Shader.PropertyToID("_DicomColorMode");
        static readonly int _ClassColorsId = Shader.PropertyToID("_DicomClassColors");
        static readonly int _LutTexId = Shader.PropertyToID("_DicomLut");
        static readonly int _BreakpointTexId = Shader.PropertyToID("_DicomBreakpointLut");
        static readonly int _BreakpointDomainId = Shader.PropertyToID("_DicomBreakpointDomain");
        static readonly int _NormalizeId = Shader.PropertyToID("_DicomNormalize");

        void Awake()
        {
            _pointCloud = GetComponent<DicomPointCloud>();
            // LUT/断点 profile 可能与其它系统(如基因显色)共享同一资产:实例化运行时副本各自持有,
            // 使烘焙纹理销毁只作用于本实例,不影响其它引用者(分类 profile 保留共享,支持编辑器写回区间)
            if (_lutProfile != null) _lutProfile = Instantiate(_lutProfile);
            if (_breakpointProfile != null) _breakpointProfile = Instantiate(_breakpointProfile);
            ApplyPalette();
            ApplyLut();
            ApplyBreakpointLut();
            ApplyNormalize();
        }

        // 从目录异步加载，全程不阻塞主线程
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

            try
            {
                // 解析在线程池；progress 来自后台线程，仅设标志位，主线程 Update 派发
                var dataset = await DicomSeriesLoader.LoadDirectoryAsync(directory, p =>
                {
                    _bgPhase = p.Phase;
                    _progressDone = p.Done;
                    _progressTotal = p.Total;
                    _bgCurrentFile = p.CurrentFile;
                    _progressDirty = true;
                }, token);

                if (token.IsCancellationRequested) return;

                _loadTimer.Stop();
                _report.LoadSeconds = (float)_loadTimer.Elapsed.TotalSeconds;
                _dataset = dataset;
                // 整卷体素一次性拷入常驻 NativeArray，后续所有重建复用同一份，避免重复分配
                SetVoxelCache(dataset);
                _report.Width = dataset.Width;
                _report.Height = dataset.Height;
                _report.Depth = dataset.Depth;

                // 采用 DICOM 元数据检测出的切片堆叠轴作为重建方向,纠正冠状/矢状序列默认按 Z 重建的错误
                _reconstructAxis = (DicomReconstructAxis)dataset.StackAxis;

                BuildPoints(dataset);

                // 加载完成后自动统计实际占用的 HU 区间,供标注分类颜色参考
                // 复用刚 SetVoxelCache 拷入的常驻 _voxels,避免再拷一份整卷副本造成加载瞬间内存尖峰
                _huReport = HuRangeAnalyzer.Analyze(dataset, _voxels);
                OnHuAnalyzed?.Invoke(_huReport);

                _report.Phase = DicomLoadPhase.Completed;
                RaiseReport();
                OnLoaded?.Invoke(dataset);
            }
            catch (OperationCanceledException)
            {
                // 主动取消，忽略
            }
            catch (Exception e)
            {
                _loadTimer.Stop();
                _report.Phase = DicomLoadPhase.Failed;
                _report.ErrorMessage = e.Message;
                _report.ErrorStack = e.StackTrace ?? "";
                RaiseReport();
                Debug.LogError($"DICOM 加载失败: {e.Message}\n{e.StackTrace}");
                OnError?.Invoke(e);
            }
        }

        void Update()
        {
            // 后台进度回到主线程派发，避免在非主线程触碰订阅者(可能更新 UI)
            if (_progressDirty)
            {
                _progressDirty = false;
                _report.Phase = _bgPhase;
                _report.FilesDone = _progressDone;
                _report.FilesTotal = _progressTotal;
                _report.CurrentFile = _bgCurrentFile;

                float ratio = _progressTotal > 0 ? (float)_progressDone / _progressTotal : 0f;
                OnProgress?.Invoke(ratio);
                RaiseReport();
            }
        }

        // 体素 -> 点：两遍式 Burst 并行(统计->前缀和->写入)，仅用核心 NativeArray
        public void BuildPoints(DicomDataset dataset)
        {
            _report.Phase = DicomLoadPhase.BuildingPoints;
            RaiseReport();
            var buildTimer = Stopwatch.StartNew();

            int sliceVoxels = dataset.Width * dataset.Height;
            int depth = dataset.Depth;

            // 复用常驻体素缓存:若与当前 dataset 不匹配(异常路径)则即时补建
            if (!_voxels.IsCreated || _voxels.Length != dataset.Voxels.Length)
                SetVoxelCache(dataset);

            // 每次重建重新分配的 job 数组，用 try/finally 保证任一步异常都不泄漏
            var sliceCounts = new NativeArray<int>(depth, Allocator.TempJob);
            var offsets = new NativeArray<int>(depth, Allocator.TempJob);
            NativeArray<DicomPoint> points = default;
            NativeArray<float3> sliceMin = default;
            NativeArray<float3> sliceMax = default;
            NativeArray<float> classMin = default;
            NativeArray<float> classMax = default;

            try
            {
                // 第一遍：每切片统计阈值内体素数
                var countJob = new CountVoxelsJob
                {
                    Voxels = _voxels,
                    SliceVoxels = sliceVoxels,
                    Slope = dataset.RescaleSlope,
                    Intercept = dataset.RescaleIntercept,
                    ThresholdMin = _thresholdMin,
                    ThresholdMax = _thresholdMax,
                    SliceCounts = sliceCounts
                };
                countJob.Schedule(depth, 1).Complete();

                // 主线程前缀和算各切片写入偏移与总点数
                int total = 0;
                for (int z = 0; z < depth; z++)
                {
                    offsets[z] = total;
                    total += sliceCounts[z];
                }

                if (total <= 0)
                {
                    _pointCloud.SetPoints(default, 0);
                    // 无可见点:尺寸归零,通知订阅者收起碰撞盒与线框
                    _localBounds = new Bounds(Vector3.zero, Vector3.zero);
                    _pointCloud.SetLocalBounds(_localBounds);
                    buildTimer.Stop();
                    _report.PointCount = 0;
                    _report.BuildSeconds = (float)buildTimer.Elapsed.TotalSeconds;
                    RaiseReport();
                    OnBoundsChanged?.Invoke(_localBounds);
                    Debug.LogWarning("DICOM 阈值过滤后无可见点");
                    return;
                }

                // 第二遍：各切片写入互不重叠区段
                points = new NativeArray<DicomPoint>(total, Allocator.TempJob);
                // 各切片可见点 AABB,主线程归约为整体局部包围盒
                sliceMin = new NativeArray<float3>(depth, Allocator.TempJob);
                sliceMax = new NativeArray<float3>(depth, Allocator.TempJob);

                // 分类区间表传给 Job(无 profile 则建空表，Job 内全部 ClassId = -1)
                BuildClassRanges(out classMin, out classMax);

                var writeJob = new WritePointsJob
                {
                    Voxels = _voxels,
                    SliceOffsets = offsets,
                    Width = dataset.Width,
                    Height = dataset.Height,
                    Depth = dataset.Depth,
                    Spacing = dataset.Spacing,
                    Slope = dataset.RescaleSlope,
                    Intercept = dataset.RescaleIntercept,
                    ThresholdMin = _thresholdMin,
                    ThresholdMax = _thresholdMax,
                    NormalizeMin = _normalizeMin,
                    NormalizeMax = _normalizeMax,
                    ReconstructAxis = (int)_reconstructAxis,
                    ClassHuMin = classMin,
                    ClassHuMax = classMax,
                    Points = points,
                    SliceMin = sliceMin,
                    SliceMax = sliceMax
                };
                writeJob.Schedule(depth, 1).Complete();

                _pointCloud.SetPoints(points, total);

                // 归约各切片 AABB 为整体局部包围盒;切片无点时为哨兵(min>max),跳过不污染结果
                float3 lo = new float3(float.MaxValue);
                float3 hi = new float3(float.MinValue);
                for (int z = 0; z < depth; z++)
                {
                    if (sliceMin[z].x > sliceMax[z].x) continue;
                    lo = math.min(lo, sliceMin[z]);
                    hi = math.max(hi, sliceMax[z]);
                }

                // 过滤后点云常偏体积一侧,包围盒中心随之偏离原点,而非固定居中
                Vector3 center = (Vector3)((lo + hi) * 0.5f);
                Vector3 size = (Vector3)(hi - lo);
                // 单点/共面时某轴尺寸为 0,给一个体素间距级最小厚度,避免碰撞盒退化与线框零厚
                float minThickness = math.cmin(dataset.Spacing);
                size.x = Mathf.Max(size.x, minThickness);
                size.y = Mathf.Max(size.y, minThickness);
                size.z = Mathf.Max(size.z, minThickness);
                _localBounds = new Bounds(center, size);
                _pointCloud.SetLocalBounds(_localBounds);

                buildTimer.Stop();
                _report.PointCount = total;
                _report.BuildSeconds = (float)buildTimer.Elapsed.TotalSeconds;
                RaiseReport();
                // 碰撞盒与线框据此紧贴当前可见点云;每次重建(调阈值/方向/归一化)都刷新
                OnBoundsChanged?.Invoke(_localBounds);

                Debug.Log($"DICOM 点云生成完毕: {total} 点 (体积 {dataset.Width}x{dataset.Height}x{dataset.Depth}, 用时 {_report.BuildSeconds:F2}s)");
            }
            finally
            {
                // 每次重建分配的临时数组全部释放，异常路径也不泄漏(常驻 _voxels 不在此释放)
                if (sliceCounts.IsCreated) sliceCounts.Dispose();
                if (offsets.IsCreated) offsets.Dispose();
                if (points.IsCreated) points.Dispose();
                if (sliceMin.IsCreated) sliceMin.Dispose();
                if (sliceMax.IsCreated) sliceMax.Dispose();
                if (classMin.IsCreated) classMin.Dispose();
                if (classMax.IsCreated) classMax.Dispose();
            }
        }

        // 把 dataset 整卷体素拷入常驻 NativeArray，替换旧缓存。加载新序列或缓存失配时调用
        void SetVoxelCache(DicomDataset dataset)
        {
            if (_voxels.IsCreated) _voxels.Dispose();
            _voxels = new NativeArray<short>(dataset.Voxels, Allocator.Persistent);
        }

        // 运行时调阈值后重算点云
        public void SetThreshold(float min, float max)
        {
            // 保证 min<=max:滑块交叉时归一化,避免过滤后恒空的静默无效状态
            if (min > max) (min, max) = (max, min);
            _thresholdMin = min;
            _thresholdMax = max;
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 运行时调归一化范围(影响点强度)，需重建点云
        public void SetNormalize(float min, float max)
        {
            // 保证 min<=max:交叉会使 denom 退化到 1e-5 令强度数值爆掉(断点模式反推真实值出错)
            if (min > max) (min, max) = (max, min);
            _normalizeMin = min;
            _normalizeMax = max;
            ApplyNormalize();
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 运行时切换重建方向(切片堆叠轴映射到的世界轴)，需重建点云
        public void SetReconstructAxis(DicomReconstructAxis axis)
        {
            _reconstructAxis = axis;
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 用当前全部设置重新生成点云，数据源未就绪时静默忽略
        public void Rebuild()
        {
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 切换显色模式：纯 shader 全局变量，零重建。LUT 模式需先绑定 _lutProfile
        public void SetColorMode(DicomColorMode mode)
        {
            Shader.SetGlobalFloat(_ColorModeId, (float)mode);
        }

        // 兼容旧调用：true=分类调色板，false=灰度强度
        public void SetColorMode(bool useClassification)
        {
            SetColorMode(useClassification ? DicomColorMode.Classification : DicomColorMode.Intensity);
        }

        // 运行时更换 LUT 配置，重新烘焙并上传纹理。传入资产则实例化副本持有,回收旧副本纹理
        public void SetLutProfile(DicomLutProfile profile)
        {
            if (_lutProfile != null) _lutProfile.DestroyBaked();
            _lutProfile = profile != null ? Instantiate(profile) : null;
            ApplyLut();
        }

        // 运行时更换断点配置，重新烘焙并上传纹理与值域。传入资产则实例化副本持有,回收旧副本纹理
        public void SetBreakpointProfile(DicomBreakpointProfile profile)
        {
            if (_breakpointProfile != null) _breakpointProfile.DestroyBaked();
            _breakpointProfile = profile != null ? Instantiate(profile) : null;
            ApplyBreakpointLut();
        }

        // 运行时更换分类配置，重建点云使新区间生效
        public void SetClassificationProfile(DicomClassificationProfile profile)
        {
            _classificationProfile = profile;
            ApplyPalette();
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 把自动识别出的 HU 占用区间写入当前绑定的 profile,生成区分色后刷新调色板并重建点云
        // 无 profile 或无分析结果时返回 false,由调用方提示
        public bool ApplyDetectedRangesToProfile()
        {
            if (_classificationProfile == null) return false;
            if (_huReport == null || _huReport.Segments.Count == 0) return false;

            int n = _huReport.Segments.Count;
            var mins = new float[n];
            var maxs = new float[n];
            for (int i = 0; i < n; i++)
            {
                mins[i] = _huReport.Segments[i].HuMin;
                maxs[i] = _huReport.Segments[i].HuMax;
            }

            _classificationProfile.SetCategoriesFromRanges(mins, maxs);
#if UNITY_EDITOR
            // 编辑器下标记脏并写盘,使 Play 模式生成的区间退出后仍保留在 asset
            UnityEditor.EditorUtility.SetDirty(_classificationProfile);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
            ApplyPalette();
            if (_dataset != null) BuildPoints(_dataset);
            return true;
        }

        // 调色板上传 shader 全局数组，类别顺序与 profile 一致
        void ApplyPalette()
        {
            if (_classificationProfile == null) return;
            Shader.SetGlobalVectorArray(_ClassColorsId, _classificationProfile.GetPalette());
        }

        // 烘焙离散 LUT 并上传 shader 全局纹理，供 LUT 显色模式采样
        void ApplyLut()
        {
            if (_lutProfile == null) return;
            Shader.SetGlobalTexture(_LutTexId, _lutProfile.BakeLut());
        }

        // 烘焙断点色带并上传 shader 全局纹理与值域，供断点显色模式采样
        void ApplyBreakpointLut()
        {
            if (_breakpointProfile == null) return;
            Shader.SetGlobalTexture(_BreakpointTexId, _breakpointProfile.BakeLut());
            Shader.SetGlobalVector(_BreakpointDomainId,
                new Vector4(_breakpointProfile.DomainMin, _breakpointProfile.DomainMax, 0f, 0f));
        }

        // 上传归一化范围,供断点模式 shader 端把 intensity 反推为真实值
        void ApplyNormalize()
        {
            Shader.SetGlobalVector(_NormalizeId, new Vector4(_normalizeMin, _normalizeMax, 0f, 0f));
        }

        // 从 profile 导出区间表为 NativeArray，无 profile 时返回零长数组
        void BuildClassRanges(out NativeArray<float> min, out NativeArray<float> max)
        {
            if (_classificationProfile == null || _classificationProfile.Count == 0)
            {
                min = new NativeArray<float>(0, Allocator.TempJob);
                max = new NativeArray<float>(0, Allocator.TempJob);
                return;
            }

            _classificationProfile.GetRanges(out var mins, out var maxs);
            min = new NativeArray<float>(mins, Allocator.TempJob);
            max = new NativeArray<float>(maxs, Allocator.TempJob);
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

        void OnDestroy()
        {
            CancelOngoing();
            // 释放常驻体素缓存，避免 NativeArray 泄漏(CLAUDE.md 资源生命周期约束)
            if (_voxels.IsCreated) _voxels.Dispose();
            // _lutProfile/_breakpointProfile 是运行时实例化副本,连同烘焙纹理一并销毁,避免纹理与 SO 泄漏
            if (_lutProfile != null) { _lutProfile.DestroyBaked(); Destroy(_lutProfile); }
            if (_breakpointProfile != null) { _breakpointProfile.DestroyBaked(); Destroy(_breakpointProfile); }
        }
    }
}
