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

        // 全基因名 -> 该基因画取比例(选区内表达该基因的 cell 数 / 全模型表达该基因的 cell 数)
        // 供区域结果搜索点选任意基因时 O(1) 查表;分母为 0(全模型无表达)记 -1 表示"无表达"
        public Dictionary<string, float> PaintFractions = new Dictionary<string, float>();

        public struct GeneScore
        {
            public string Gene;
            public float MeanExpression;
        }
    }
}
