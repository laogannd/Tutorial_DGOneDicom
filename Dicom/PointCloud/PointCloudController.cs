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
    // 协调完整管线：后台解析 DICOM -> Burst 体素转点 -> 上传 ComputeBuffer -> 渲染
    // 全程维护 DicomLoadReport 诊断快照，供调试面板读取阶段、进度、耗时与错误
    [RequireComponent(typeof(DicomPointCloud))]
    public class PointCloudController : MonoBehaviour
    {
        [SerializeField] float _thresholdMin = 200f;
        [SerializeField] float _thresholdMax = 3000f;
        [SerializeField] float _normalizeMin = 200f;
        [SerializeField] float _normalizeMax = 1500f;

        // HU 区间分类配置，未绑定则不分类(所有点 ClassId = -1)
        [SerializeField] DicomClassificationProfile _classificationProfile;

        public event Action<float> OnProgress;
        public event Action<DicomDataset> OnLoaded;
        public event Action<Exception> OnError;
        // 阶段或诊断信息变化时触发，调试面板据此刷新
        public event Action<DicomLoadReport> OnReportChanged;
        // HU 区间分析完成时触发,携带直方图与自动识别的占用区间
        public event Action<HuRangeReport> OnHuAnalyzed;

        DicomPointCloud _pointCloud;
        DicomDataset _dataset;
        HuRangeReport _huReport;
        CancellationTokenSource _cts;

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

        public float ThresholdMin => _thresholdMin;
        public float ThresholdMax => _thresholdMax;
        public float NormalizeMin => _normalizeMin;
        public float NormalizeMax => _normalizeMax;
        public DicomClassificationProfile ClassificationProfile => _classificationProfile;

        static readonly int _ColorModeId = Shader.PropertyToID("_DicomColorMode");
        static readonly int _ClassColorsId = Shader.PropertyToID("_DicomClassColors");

        void Awake()
        {
            _pointCloud = GetComponent<DicomPointCloud>();
            ApplyPalette();
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
                _report.Width = dataset.Width;
                _report.Height = dataset.Height;
                _report.Depth = dataset.Depth;

                BuildPoints(dataset);

                // 加载完成后自动统计实际占用的 HU 区间,供标注分类颜色参考
                _huReport = HuRangeAnalyzer.Analyze(dataset);
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

            // 体积世界尺寸(与 WritePointsJob 的位置计算一致)，供点云 bounds 剔除
            _pointCloud.SetLocalSize(new Vector3(
                dataset.Width * dataset.Spacing.x,
                dataset.Height * dataset.Spacing.y,
                dataset.Depth * dataset.Spacing.z));

            var voxels = new NativeArray<short>(dataset.Voxels, Allocator.TempJob);
            var sliceCounts = new NativeArray<int>(depth, Allocator.TempJob);

            // 第一遍：每切片统计阈值内体素数
            var countJob = new CountVoxelsJob
            {
                Voxels = voxels,
                SliceVoxels = sliceVoxels,
                Slope = dataset.RescaleSlope,
                Intercept = dataset.RescaleIntercept,
                ThresholdMin = _thresholdMin,
                ThresholdMax = _thresholdMax,
                SliceCounts = sliceCounts
            };
            countJob.Schedule(depth, 1).Complete();

            // 主线程前缀和算各切片写入偏移与总点数
            var offsets = new NativeArray<int>(depth, Allocator.TempJob);
            int total = 0;
            for (int z = 0; z < depth; z++)
            {
                offsets[z] = total;
                total += sliceCounts[z];
            }

            if (total <= 0)
            {
                _pointCloud.SetPoints(default, 0);
                voxels.Dispose();
                sliceCounts.Dispose();
                offsets.Dispose();
                buildTimer.Stop();
                _report.PointCount = 0;
                _report.BuildSeconds = (float)buildTimer.Elapsed.TotalSeconds;
                RaiseReport();
                Debug.LogWarning("DICOM 阈值过滤后无可见点");
                return;
            }

            // 第二遍：各切片写入互不重叠区段
            var points = new NativeArray<DicomPoint>(total, Allocator.TempJob);

            // 分类区间表传给 Job(无 profile 则建空表，Job 内全部 ClassId = -1)
            BuildClassRanges(out var classMin, out var classMax);

            var writeJob = new WritePointsJob
            {
                Voxels = voxels,
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
                ClassHuMin = classMin,
                ClassHuMax = classMax,
                Points = points
            };
            writeJob.Schedule(depth, 1).Complete();

            _pointCloud.SetPoints(points, total);

            // 资源释放，避免 NativeArray 泄漏(CLAUDE.md 约束)
            points.Dispose();
            classMin.Dispose();
            classMax.Dispose();
            offsets.Dispose();
            sliceCounts.Dispose();
            voxels.Dispose();

            buildTimer.Stop();
            _report.PointCount = total;
            _report.BuildSeconds = (float)buildTimer.Elapsed.TotalSeconds;
            RaiseReport();

            Debug.Log($"DICOM 点云生成完毕: {total} 点 (体积 {dataset.Width}x{dataset.Height}x{dataset.Depth}, 用时 {_report.BuildSeconds:F2}s)");
        }

        // 运行时调阈值后重算点云
        public void SetThreshold(float min, float max)
        {
            _thresholdMin = min;
            _thresholdMax = max;
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 运行时调归一化范围(影响点强度)，需重建点云
        public void SetNormalize(float min, float max)
        {
            _normalizeMin = min;
            _normalizeMax = max;
            if (_dataset != null) BuildPoints(_dataset);
        }

        // 切换分类着色模式：纯 shader 全局变量，零重建
        public void SetColorMode(bool useClassification)
        {
            Shader.SetGlobalFloat(_ColorModeId, useClassification ? 1f : 0f);
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

        void OnDestroy() => CancelOngoing();
    }
}
