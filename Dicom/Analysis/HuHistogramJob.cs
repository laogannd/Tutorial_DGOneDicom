using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dicom.Analysis
{
    // 对全体素做 HU 直方图统计,按 z 切片并行,每切片写入自己专属的 [z*Bins,(z+1)*Bins) 区段
    // 区段互不重叠,故可安全关闭并行写入限制(仿 WritePointsJob 写法),仅用核心 NativeArray
    // HU 真实值 = stored * Slope + Intercept,越界值 clamp 到首尾 bin
    [BurstCompile]
    public struct HuHistogramJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<short> Voxels;
        public int SliceVoxels;
        public int Bins;
        public float Slope;
        public float Intercept;
        public float HuStart;
        public float BinWidth;

        // 长度 = Depth * Bins,各切片独占一段,主线程后续按 bin 求和归约
        [NativeDisableParallelForRestriction] public NativeArray<int> PerSliceBins;

        // index 为切片号 z
        public void Execute(int z)
        {
            int voxelStart = z * SliceVoxels;
            int binStart = z * Bins;
            float invWidth = 1f / BinWidth;
            int last = Bins - 1;

            for (int i = 0; i < SliceVoxels; i++)
            {
                float hu = Voxels[voxelStart + i] * Slope + Intercept;
                int bin = (int)((hu - HuStart) * invWidth);
                bin = math.clamp(bin, 0, last);
                PerSliceBins[binStart + bin]++;
            }
        }
    }
}
