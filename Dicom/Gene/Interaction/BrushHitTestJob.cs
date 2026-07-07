using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dicom.Gene
{
    // 画笔命中测试:136k cell 对球/盒做包含判断,写选中掩码
    // 在 local 空间测试(球心/盒经 worldToLocal 转 local),避免每 cell 世界变换
    // Mask 累积不清零:涂抹扫过持续 OR 置位;清除选择由外部填 0

    // 球形笔刷:cell 到球心 local 距离平方 <= 半径平方则置位
    [BurstCompile]
    public struct BrushSphereJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        public float3 CenterLocal;
        public float RadiusLocalSq;
        public NativeArray<byte> Mask;

        public void Execute(int i)
        {
            if (math.distancesq(CellPos[i], CenterLocal) <= RadiusLocalSq)
                Mask[i] = 1;
        }
    }

    // 盒框选:cell 落入 local AABB [Min,Max] 则置位
    [BurstCompile]
    public struct BrushBoxJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        public float3 MinLocal;
        public float3 MaxLocal;
        public NativeArray<byte> Mask;

        public void Execute(int i)
        {
            float3 p = CellPos[i];
            if (math.all(p >= MinLocal) && math.all(p <= MaxLocal))
                Mask[i] = 1;
        }
    }
}
