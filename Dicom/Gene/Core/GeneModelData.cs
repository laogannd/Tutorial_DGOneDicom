using Unity.Collections;
using Unity.Mathematics;

namespace Dicom.Gene
{
    // cell 空间模型数据:cell_mapping.json 解析结果
    // 托管数组(Grid/Tag)由后台线程产出;主线程据此构建常驻 NativeArray(CellPos/CellTag)供 Burst 使用
    // cellId 连续 0..CellCount-1,所有数组按 cellId 索引
    public sealed class GeneModelData
    {
        public int CellCount;

        // 后台线程产出的托管数据
        public int3[] Grid;   // 各 cell 网格坐标
        public int[] Tag;     // 各 cell 区域标签数值

        // 网格坐标包围范围,用于居中布局与算 bounds
        public int3 GridMin;
        public int3 GridMax;

        // 主线程构建的常驻数据(Allocator.Persistent),全生命周期复用
        // CellPos 为已居中的 local 坐标(mm),CellTag 供区域统计
        public NativeArray<float3> CellPos;
        public NativeArray<int> CellTag;

        public bool NativeReady => CellPos.IsCreated && CellTag.IsCreated;

        // 释放常驻 NativeArray,GeneColorController 销毁或换模型时调用
        public void DisposeNative()
        {
            if (CellPos.IsCreated) CellPos.Dispose();
            if (CellTag.IsCreated) CellTag.Dispose();
        }
    }
}
