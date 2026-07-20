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
    // 职责分离:有掩码时主点云只渲染已画取 cell(不透明彩色,叠上层);未画取区域交给灰色幽灵底图呈现,
    // 不再由主点云用淡显彩色重复渲染(那样密集叠加累积成不透明,既盖住幽灵又看不出画没画)

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

            // 无掩码=全量渲染;有掩码=只渲染已画取 cell(未画取留给幽灵底图)
            int count = 0;
            for (int i = start; i < end; i++)
            {
                float v = Values[i];
                if (math.isnan(v)) continue;
                if (useMask && Mask[i] == 0) continue;
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
                float v = Values[i];
                if (math.isnan(v)) continue;

                float3 pos = CellPos[i];
                // AABB 按全部表达 cell 累积(不受掩码影响),使抓取碰撞盒稳定贴合完整表达模型,
                // 不因画取多少而突变缩放
                lo = math.min(lo, pos);
                hi = math.max(hi, pos);

                // 有掩码时只写已画取 cell,与 GeneCountJob 过滤一致(未画取交给幽灵底图)
                if (useMask && Mask[i] == 0) continue;

                float intensity = (v - NormalizeMin) / denom;
                // 输出的都是已画取(或无掩码全量),恒为不透明彩色,叠在灰色幽灵底图上层
                Points[write++] = new DicomPoint
                {
                    Position = pos,
                    Intensity = intensity,
                    ClassId = CellTag[i],
                    Selected = 1f
                };
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
                    ClassId = -1f,
                    Selected = 1f
                };
            }
        }
    }

    // 幽灵点云构建(无基因回退):渲染全模型全部 cell,恒定强度 + Selected=0(全走半透明淡显 Pass)
    // 供 GeneBrushVisual 底图:完整模型以灰白半透明常驻(不写深度故不遮挡),已画取的不透明彩色点
    // 由主点云(选基因走 LUT 表达色)或 overlay(未选基因走高亮色)叠在上层覆盖,得"完整点云"效果
    // 因渲染全部 cell,输出下标==cell 下标,无需计数/前缀和,单遍按 cell 下标直接写入
    [BurstCompile]
    public struct GeneGhostWriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        public int BlockSize;
        public int CellCount;
        public float Intensity;

        [NativeDisableParallelForRestriction] public NativeArray<DicomPoint> Points;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);
            for (int i = start; i < end; i++)
            {
                Points[i] = new DicomPoint
                {
                    Position = CellPos[i],
                    Intensity = Intensity,
                    ClassId = -1f,
                    Selected = 0f
                };
            }
        }
    }

    // 幽灵点云构建(选中基因):未画取底图按表达值走主点云同款配色半透明。渲染有表达值(非 NaN)的全部 cell,
    // 每 cell 写归一化表达强度 + Selected=0(走淡显 Pass,由 GeneBrushVisual 复制主点云 LUT + 可量化 alpha)。
    // 已画取的 cell 由主点云(当前配色不透明)叠在上层覆盖,得"已画不透明 + 未画同配色透明"效果。
    // 与掩码无关(全表达 cell 都画,画取的被主点云盖住),故只随基因切换重建,不随每笔画取重建。
    // 跳过 NaN 故非全量,需计数(复用 GeneCountJob 传零长掩码)+ 前缀和后各块写专属区段
    [BurstCompile]
    public struct GeneGhostExprWriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        [ReadOnly] public NativeArray<float> Values;
        [ReadOnly] public NativeArray<int> CellTag;
        [ReadOnly] public NativeArray<int> BlockOffsets;

        public int BlockSize;
        public int CellCount;
        public float NormalizeMin;
        public float NormalizeMax;

        [NativeDisableParallelForRestriction] public NativeArray<DicomPoint> Points;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);
            int write = BlockOffsets[block];
            float denom = math.max(NormalizeMax - NormalizeMin, 1e-5f);

            for (int i = start; i < end; i++)
            {
                float v = Values[i];
                if (math.isnan(v)) continue;

                float intensity = (v - NormalizeMin) / denom;
                Points[write++] = new DicomPoint
                {
                    Position = CellPos[i],
                    Intensity = intensity,
                    ClassId = CellTag[i],
                    Selected = 0f
                };
            }
        }
    }
}
