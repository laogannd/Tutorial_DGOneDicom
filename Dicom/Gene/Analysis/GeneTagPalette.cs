using UnityEngine;

namespace Dicom.Gene
{
    // tag 数值 -> 稳定分类颜色:黄金比例分数打散色相,保证相邻 tag 颜色区分度高且确定性
    // 供笔刷指示球/空间文本按当前所属标记部位着色;与基因表达 LUT 显色无关
    public static class GeneTagPalette
    {
        // 黄金比例共轭,累乘取小数部分得低差异序列,色相分布均匀
        const float GoldenRatioConjugate = 0.618033988749895f;

        public static Color Color(int tag)
        {
            int t = tag < 0 ? -tag : tag;
            float hue = Frac(t * GoldenRatioConjugate);
            return UnityEngine.Color.HSVToRGB(hue, 0.6f, 0.95f);
        }

        static float Frac(float x) => x - Mathf.Floor(x);
    }
}
