using System.Collections.Generic;

namespace Dicom.Analysis
{
    // HU 区间分析结果,纯数据。加载完成后由 HuRangeAnalyzer 填充,调试面板读取展示
    public class HuRangeReport
    {
        // 直方图原始数据:Bins[i] 对应 HU 区间 [HuStart + i*BinWidth, HuStart + (i+1)*BinWidth)
        public int[] Bins;
        public float HuStart;
        public float BinWidth;
        public int BinCount;

        public int TotalVoxels;
        // 直方图单 bin 最大计数,供面板归一化柱高
        public int MaxBinCount;

        // 自动识别出的连续占用 HU 区间,按 HU 升序
        public List<HuSegment> Segments = new List<HuSegment>();
    }

    // 单个被占用的 HU 区间:[HuMin, HuMax) 内体素数 VoxelCount,占全体素比例 Fraction
    public struct HuSegment
    {
        public float HuMin;
        public float HuMax;
        public int VoxelCount;
        public float Fraction;
    }
}
