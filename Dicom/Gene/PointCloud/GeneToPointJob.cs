using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

using Dicom.PointCloud;

namespace Dicom.Gene
{
    // cell -> DicomPoint 的两遍式 Burst 构建,复用 DicomPoint 结构与渲染管线
    // 136k cell 无天然分组,按固定块并行:第一遍每块统计合格点数,主线程前缀和,第二遍各块写专属区段
    // 合格 = 表达值非 NaN 且(无掩码 或 掩码置位)。表达值按 [Min,Max] 归一化写 Intensity,tag 写 ClassId 备用

    // 第一遍:每块统计合格 cell 数
    [BurstCompile]
    public struct GeneCountJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Values;
        [ReadOnly] public NativeArray<byte> Mask;   // 长度 0 表示无掩码(全选)
        public int BlockSize;
        public int CellCount;
        [WriteOnly] public NativeArray<int> BlockCounts;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);
            bool useMask = Mask.Length > 0;

            int count = 0;
            for (int i = start; i < end; i++)
            {
                if (useMask && Mask[i] == 0) continue;
                float v = Values[i];
                if (math.isnan(v)) continue;
                count++;
            }
            BlockCounts[block] = count;
        }
    }

    // 第二遍:每块把合格 cell 写入 [offset, offset+count) 专属区段,区段互不重叠
    [BurstCompile]
    public struct GeneWriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        [ReadOnly] public NativeArray<float> Values;
        [ReadOnly] public NativeArray<int> CellTag;
        [ReadOnly] public NativeArray<byte> Mask;
        [ReadOnly] public NativeArray<int> BlockOffsets;

        public int BlockSize;
        public int CellCount;
        public float NormalizeMin;
        public float NormalizeMax;

        [NativeDisableParallelForRestriction] public NativeArray<DicomPoint> Points;

        // 每块合格点局部 AABB,无点写哨兵(min=+inf max=-inf)供主线程归约忽略
        [WriteOnly] public NativeArray<float3> BlockMin;
        [WriteOnly] public NativeArray<float3> BlockMax;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);
            int write = BlockOffsets[block];
            bool useMask = Mask.Length > 0;
            float denom = math.max(NormalizeMax - NormalizeMin, 1e-5f);

            float3 lo = new float3(float.MaxValue);
            float3 hi = new float3(float.MinValue);

            for (int i = start; i < end; i++)
            {
                if (useMask && Mask[i] == 0) continue;
                float v = Values[i];
                if (math.isnan(v)) continue;

                float3 pos = CellPos[i];
                float intensity = (v - NormalizeMin) / denom;

                Points[write++] = new DicomPoint
                {
                    Position = pos,
                    Intensity = intensity,
                    ClassId = CellTag[i]
                };

                lo = math.min(lo, pos);
                hi = math.max(hi, pos);
            }

            BlockMin[block] = lo;
            BlockMax[block] = hi;
        }
    }

    // 选中高亮 overlay 构建:只按掩码取 cell,不依赖表达值,写恒定强度(渲染为 colormap 顶端亮色)
    // 供 GeneBrushVisual 的第二 DicomPointCloud 高亮当前选区,与基因表达显色解耦

    // overlay 第一遍:每块统计掩码置位数
    [BurstCompile]
    public struct GeneOverlayCountJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Mask;
        public int BlockSize;
        public int CellCount;
        [WriteOnly] public NativeArray<int> BlockCounts;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);
            int count = 0;
            for (int i = start; i < end; i++)
                if (Mask[i] != 0) count++;
            BlockCounts[block] = count;
        }
    }

    // overlay 第二遍:每块把掩码置位 cell 写入专属区段,恒定强度
    [BurstCompile]
    public struct GeneOverlayWriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        [ReadOnly] public NativeArray<byte> Mask;
        [ReadOnly] public NativeArray<int> BlockOffsets;
        public int BlockSize;
        public int CellCount;
        public float Intensity;

        [NativeDisableParallelForRestriction] public NativeArray<DicomPoint> Points;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);
            int write = BlockOffsets[block];
            for (int i = start; i < end; i++)
            {
                if (Mask[i] == 0) continue;
                Points[write++] = new DicomPoint
                {
                    Position = CellPos[i],
                    Intensity = Intensity,
                    ClassId = -1f
                };
            }
        }
    }
}
