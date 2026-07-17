namespace Dicom.Gene
{
    // 单个基因的表达数据:Values 按 cellId 索引(与 GeneModelData 的 cell 顺序一致),长度=CellCount
    // Min/Max 为该基因表达值域,供归一化铺满 colormap;纯托管数据,后台线程解析产出
    public sealed class GeneExpression
    {
        public string GeneName;

        // 各 cell 的表达强度,索引即 cellId;缺失的 cell 填 float.NaN 供构建时跳过
        public float[] Values;

        // 有效表达值(非 NaN)的最小/最大,归一化用
        public float Min;
        public float Max;

        // 全模型内表达该基因(值非 NaN 且 > 0)的 cell 数,供区域画取比例做分母;解析时顺带统计
        public int ExpressedCount;

        public int Count => Values != null ? Values.Length : 0;
    }
}
