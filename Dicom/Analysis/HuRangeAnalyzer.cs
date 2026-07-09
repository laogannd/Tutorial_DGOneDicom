using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

using Dicom.Core;

namespace Dicom.Analysis
{
    // 加载完成后对体数据做 HU 直方图统计并自动识别连续占用的 HU 区间
    // 在主线程调用(BuildPoints 之后),内部 Burst Job 并行统计,几十毫秒级,仅用核心 NativeArray
    public static class HuRangeAnalyzer
    {
        // 直方图固定范围,覆盖 CT 常见 HU:空气-1000 到致密骨/金属 3000+
        const float HuStart = -1024f;
        const float HuEnd = 3072f;
        const int BinCount = 512; // 每 bin 宽 8 HU

        // 分段参数
        const float OccupiedFraction = 0.0005f; // bin 占比超过此值视为被占用,滤除噪声
        const int MaxGapBins = 2;               // 允许的小间隙 bin 数,避免区间碎裂
        const float MinSegmentFraction = 0.002f; // 段总占比下限,滤除过小段
        const int MaxSegments = DicomClassificationProfile.MaxCategories;

        // 分析数据集,返回填充好的报告。dataset 为空或无体素时返回空报告
        // 复用调用方已常驻的体素 NativeArray,避免为直方图统计再拷一份整卷副本(大体积内存尖峰/OOM)
        public static HuRangeReport Analyze(DicomDataset dataset, NativeArray<short> voxels)
        {
            var report = new HuRangeReport
            {
                HuStart = HuStart,
                BinWidth = (HuEnd - HuStart) / BinCount,
                BinCount = BinCount,
                Bins = new int[BinCount]
            };

            if (dataset == null || !voxels.IsCreated || voxels.Length == 0)
                return report;

            int sliceVoxels = dataset.Width * dataset.Height;
            int depth = dataset.Depth;

            var perSliceBins = new NativeArray<int>(depth * BinCount, Allocator.TempJob);

            var job = new HuHistogramJob
            {
                Voxels = voxels,
                SliceVoxels = sliceVoxels,
                Bins = BinCount,
                Slope = dataset.RescaleSlope,
                Intercept = dataset.RescaleIntercept,
                HuStart = HuStart,
                BinWidth = report.BinWidth,
                PerSliceBins = perSliceBins
            };
            job.Schedule(depth, 1).Complete();

            // 主线程归约:各切片同 bin 求和到总直方图
            int total = 0;
            int maxBin = 0;
            for (int b = 0; b < BinCount; b++)
            {
                int sum = 0;
                for (int z = 0; z < depth; z++)
                    sum += perSliceBins[z * BinCount + b];
                report.Bins[b] = sum;
                total += sum;
                if (sum > maxBin) maxBin = sum;
            }
            report.TotalVoxels = total;
            report.MaxBinCount = maxBin;

            // voxels 由调用方持有,此处不释放;仅释放本方法分配的临时直方图
            perSliceBins.Dispose();

            BuildSegments(report);
            return report;
        }

        // 把连续被占用的 bin 合并成 HU 区间:占比超阈值的 bin 视为占用,允许小间隙桥接
        static void BuildSegments(HuRangeReport report)
        {
            int total = report.TotalVoxels;
            if (total <= 0) return;

            float occupiedCount = total * OccupiedFraction;
            var raw = new List<(int startBin, int endBin, int count)>();

            int segStart = -1;
            int segCount = 0;
            int gap = 0;

            for (int b = 0; b < report.BinCount; b++)
            {
                bool occupied = report.Bins[b] >= occupiedCount;
                if (occupied)
                {
                    if (segStart < 0) segStart = b;
                    segCount += report.Bins[b];
                    gap = 0;
                }
                else if (segStart >= 0)
                {
                    // 段内统计已含间隙 bin 的计数,保持区间连贯
                    gap++;
                    segCount += report.Bins[b];
                    if (gap > MaxGapBins)
                    {
                        // 间隙过大,收尾当前段,回退掉尾部纯间隙 bin
                        raw.Add((segStart, b - gap, segCount));
                        segStart = -1;
                        segCount = 0;
                        gap = 0;
                    }
                }
            }
            if (segStart >= 0)
                raw.Add((segStart, report.BinCount - 1 - gap, segCount));

            // 过滤过小段并转 HU 区间,超出上限时保留体素量最大的若干段
            var segments = new List<HuSegment>();
            float minCount = total * MinSegmentFraction;
            foreach (var (startBin, endBin, count) in raw)
            {
                if (count < minCount) continue;
                segments.Add(new HuSegment
                {
                    HuMin = report.HuStart + startBin * report.BinWidth,
                    HuMax = report.HuStart + (endBin + 1) * report.BinWidth,
                    VoxelCount = count,
                    Fraction = (float)count / total
                });
            }

            if (segments.Count > MaxSegments)
            {
                segments.Sort((a, b) => b.VoxelCount.CompareTo(a.VoxelCount));
                segments.RemoveRange(MaxSegments, segments.Count - MaxSegments);
                segments.Sort((a, b) => a.HuMin.CompareTo(b.HuMin));
            }

            report.Segments = segments;
        }
    }
}
