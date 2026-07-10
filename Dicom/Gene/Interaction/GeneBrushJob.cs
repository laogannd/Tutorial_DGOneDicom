using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dicom.Gene
{
    // 3D 球形空间画笔命中测试(分块并行):每 cell local->world,到笔刷世界球心距离在半径内则置位
    // 一遍完成三件事:1)半径内 cell 累积置位 Mask(OR) 2)每块统计当前 Mask 总置位数 3)每块找半径内离球心最近的 cell 的 tag
    // 主线程只对块级结果做小规模归约(~数十块),避免每帧扫 136k
    // LocalToWorld/球心/半径均为世界空间(米);模型均匀缩放,世界空间直接比距离无拉伸
    // 各块写各自不相交的 index 区段,故 Mask 加 NativeDisableParallelForRestriction 安全
    [BurstCompile]
    public struct GeneBrushJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        [ReadOnly] public NativeArray<int> CellTag;
        // cell local -> world,主线程一次算好传入
        public float4x4 LocalToWorld;
        // 笔刷世界球心
        public float3 BrushCenterWorld;
        // 半径平方(世界米^2),用平方距离比较省开方
        public float RadiusSqWorld;

        public int BlockSize;
        public int CellCount;

        [NativeDisableParallelForRestriction]
        public NativeArray<byte> Mask;

        // 每块:当前 Mask 总置位数
        [WriteOnly] public NativeArray<int> BlockSelected;
        // 每块:半径内离球心最近 cell 的距离平方(无命中写 +inf)与其 tag
        [WriteOnly] public NativeArray<float> BlockNearestDistSq;
        [WriteOnly] public NativeArray<int> BlockNearestTag;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, CellCount);

            float nearestDistSq = float.MaxValue;
            int nearestTag = 0;

            for (int i = start; i < end; i++)
            {
                float3 world = math.mul(LocalToWorld, new float4(CellPos[i], 1f)).xyz;
                float d = math.distancesq(world, BrushCenterWorld);
                if (d <= RadiusSqWorld)
                {
                    Mask[i] = 1;
                    if (d < nearestDistSq)
                    {
                        nearestDistSq = d;
                        nearestTag = CellTag[i];
                    }
                }
            }

            int selected = 0;
            for (int i = start; i < end; i++)
                if (Mask[i] != 0) selected++;

            BlockSelected[block] = selected;
            BlockNearestDistSq[block] = nearestDistSq;
            BlockNearestTag[block] = nearestTag;
        }
    }
}
