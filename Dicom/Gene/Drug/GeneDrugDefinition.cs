using System;

namespace Dicom.Gene
{
    // 单个药物定义:对现有基因表达强度 json 做参数化变换,不引入新数据文件
    // 效应强度 e = (dose / MaxDose)^Hill,逐 cell 变换:
    //   scale = lerp(1, GlobalScale * 靶基因倍率 * 靶区域倍率, e)
    //   v' = max(0, v * scale + Bias * e)
    // 归一化基准仍用无药物时的基因值域,故加药整体推向 colormap 高端、抑制退向低端,视觉上就是整体变色
    [Serializable]
    public class GeneDrugDefinition
    {
        // 药名(面板按钮显示)与说明
        public string Name = "未命名药物";
        public string Description = "";

        // 剂量范围:面板滑条 0..MaxDose,DefaultDose 为选中该药时的初始剂量
        public float MaxDose = 1f;
        public float DefaultDose = 1f;

        // 满剂量时对全部 cell 的整体倍率(>1 增强表达,<1 抑制表达)
        public float GlobalScale = 1f;
        // 满剂量时对全部 cell 的整体偏移(表达值单位,负值可把弱表达压到 0)
        public float Bias = 0f;

        // 靶基因名(为空则对任意基因一视同仁);命中时叠加 TargetGeneScale
        public string[] TargetGenes = Array.Empty<string>();
        public float TargetGeneScale = 1f;

        // 靶区域 tag(为空则对全部区域一视同仁);命中时该区域 cell 叠加 TargetTagScale
        public int[] TargetTags = Array.Empty<int>();
        public float TargetTagScale = 1f;

        // 剂量-效应曲线指数:1=线性,>1 低剂量迟钝(阈值感),<1 低剂量即接近饱和
        public float Hill = 1f;

        // 剂量归一化到效应强度 0..1
        public float EffectStrength(float dose)
        {
            float maxDose = MaxDose > 0f ? MaxDose : 1f;
            float ratio = dose / maxDose;
            if (ratio <= 0f) return 0f;
            if (ratio > 1f) ratio = 1f;
            if (Hill == 1f) return ratio;
            return (float)Math.Pow(ratio, Hill);
        }

        // 是否命中靶基因(靶列表为空视为命中,即对全部基因生效);大小写不敏感
        public bool MatchesGene(string geneName)
        {
            if (TargetGenes == null || TargetGenes.Length == 0) return true;
            if (string.IsNullOrEmpty(geneName)) return false;
            for (int i = 0; i < TargetGenes.Length; i++)
                if (string.Equals(TargetGenes[i], geneName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
