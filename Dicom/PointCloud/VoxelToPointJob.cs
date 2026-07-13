using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dicom.PointCloud
{
    // GPU 点结构，与 shader StructuredBuffer 布局一致：12B 位置 + 4B 强度 + 4B 类别 + 4B 选中 = 24B
    public struct DicomPoint
    {
        public float3 Position;
        public float Intensity;
        // 组织分类索引，由 DicomClassificationProfile 区间表查得；-1 表示未分类
        public float ClassId;
        // 选中标志：1=不透明(选中/常规), 0=按 _DicomAlpha 半透明淡显。DICOM 恒为 1,基因按掩码
        public float Selected;
    }

    // 两遍式体素转点，仅用核心 NativeArray，无 com.unity.collections 依赖
    // 第一遍按 z 切片并行统计合格体素数，主线程前缀和算偏移，第二遍按偏移并行写入
    // 输出数组按实际点数分配，避免按体素总数(可达数千万)预留内存

    // 第一遍：每个切片统计阈值内的体素数
    [BurstCompile]
    public struct CountVoxelsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<short> Voxels;
        public int SliceVoxels;
        public float Slope;
        public float Intercept;
        public float ThresholdMin;
        public float ThresholdMax;
        [WriteOnly] public NativeArray<int> SliceCounts;

        // index 为切片号 z
        public void Execute(int z)
        {
            int start = z * SliceVoxels;
            int count = 0;
            for (int i = 0; i < SliceVoxels; i++)
            {
                float real = Voxels[start + i] * Slope + Intercept;
                if (real >= ThresholdMin && real <= ThresholdMax)
                    count++;
            }
            SliceCounts[z] = count;
        }
    }

    // 第二遍：每个切片把合格体素写入 [offset, offset+count) 的专属区段，区段互不重叠
    [BurstCompile]
    public struct WritePointsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<short> Voxels;
        [ReadOnly] public NativeArray<int> SliceOffsets;

        public int Width;
        public int Height;
        public int Depth;
        public float3 Spacing;
        public float Slope;
        public float Intercept;
        public float ThresholdMin;
        public float ThresholdMax;
        public float NormalizeMin;
        public float NormalizeMax;

        // 切片堆叠轴指向的世界轴：0=X 1=Y 2=Z(默认)，决定点云按哪个方向重建
        public int ReconstructAxis;

        // 分类区间表(HU 下限/上限)，长度即类别数；为空则所有点 ClassId = -1
        [ReadOnly] public NativeArray<float> ClassHuMin;
        [ReadOnly] public NativeArray<float> ClassHuMax;

        // 各切片写入互不重叠的区段，故可安全关闭并行写入限制
        [NativeDisableParallelForRestriction] public NativeArray<DicomPoint> Points;

        // 每切片合格点的局部 AABB,按 z 索引各写各的无冲突;无合格点的切片写哨兵(min=+inf max=-inf)供主线程归约忽略
        [WriteOnly] public NativeArray<float3> SliceMin;
        [WriteOnly] public NativeArray<float3> SliceMax;

        public void Execute(int z)
        {
            int sliceVoxels = Width * Height;
            int start = z * sliceVoxels;
            int write = SliceOffsets[z];
            float3 half = new float3(Width, Height, Depth) * 0.5f;
            float denom = math.max(NormalizeMax - NormalizeMin, 1e-5f);
            int classCount = ClassHuMin.Length;

            // 哨兵初值,本切片有点时被收窄,无点则原样写出供归约忽略
            float3 lo = new float3(float.MaxValue);
            float3 hi = new float3(float.MinValue);

            for (int i = 0; i < sliceVoxels; i++)
            {
                float real = Voxels[start + i] * Slope + Intercept;
                if (real < ThresholdMin || real > ThresholdMax)
                    continue;

                int x = i % Width;
                int y = i / Width;
                float3 pos = (new float3(x, y, z) - half) * Spacing;
                // 切片堆叠轴(z)与目标世界轴互换，实现按 X/Y/Z 重建方向加载
                pos = RemapAxis(pos);
                // 存原始归一化值(不 saturate):灰度/LUT/分类模式 shader 端会再 saturate,无副作用
                // 断点模式据此反推真实值 real = intensity*(NormMax-NormMin)+NormMin
                float intensity = (real - NormalizeMin) / denom;
                float classId = ResolveClass(real, classCount);

                // DICOM 点恒不透明
                Points[write++] = new DicomPoint { Position = pos, Intensity = intensity, ClassId = classId, Selected = 1f };

                // 累积本切片可见点的真实 AABB,坐标已经过 RemapAxis,故与重建方向一致
                lo = math.min(lo, pos);
                hi = math.max(hi, pos);
            }

            SliceMin[z] = lo;
            SliceMax[z] = hi;
        }

        // 顺序查区间，命中即返回索引，未命中返回 -1
        float ResolveClass(float hu, int classCount)
        {
            for (int c = 0; c < classCount; c++)
                if (hu >= ClassHuMin[c] && hu < ClassHuMax[c]) return c;
            return -1f;
        }

        // 把切片堆叠轴(局部 z)交换到目标世界轴：0=X 与 x 互换，1=Y 与 y 互换，2=Z 保持原样
        float3 RemapAxis(float3 p)
        {
            switch (ReconstructAxis)
            {
                case 0: return new float3(p.z, p.y, p.x);
                case 1: return new float3(p.x, p.z, p.y);
                default: return p;
            }
        }
    }
}
