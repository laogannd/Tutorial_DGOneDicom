using System.Collections.Generic;

namespace Dicom.Gene
{
    // mode2 区域分析结果:主导区域标签 + 人类可读名 + 区域内前 N 强表达基因
    public sealed class GeneRegionReport
    {
        // 选中 cell 的多数 tag(区域识别结果)
        public int DominantTag;
        // tag 对应的人类可读名(经 GeneTagNameTable 查,未命中回退 "区域{tag}")
        public string RegionName;
        // 选中 cell 数
        public int CellCount;
        // 区域内表达均值最高的若干基因(降序),元素为(基因名,均值)
        public List<GeneScore> TopGenes = new List<GeneScore>();

        public struct GeneScore
        {
            public string Gene;
            public float MeanExpression;
        }
    }
}
