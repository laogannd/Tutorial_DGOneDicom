using Unity.Mathematics;

namespace Dicom.Core
{
    // DICOM 序列解析结果容器，纯数据，体素按 z 切片顺序线性排列
    public class DicomDataset
    {
        public int Width;
        public int Height;
        public int Depth;

        // 体素物理间距(mm)：xy 来自 PixelSpacing，z 来自 SliceThickness 或切片间距
        public float3 Spacing = new float3(1f, 1f, 1f);

        // 像素值线性变换：真实值 = stored * RescaleSlope + RescaleIntercept
        public float RescaleSlope = 1f;
        public float RescaleIntercept = 0f;

        // 默认窗宽窗位(HU 或线性变换后单位)
        public float WindowCenter = 40f;
        public float WindowWidth = 400f;

        // 原始体素，已乘斜率截距前的存储值，short 覆盖 CT 的 HU 范围
        public short[] Voxels;

        public int VoxelCount => Width * Height * Depth;

        // 线性索引，按 x 最快、z 最慢
        public int Index(int x, int y, int z) => (z * Height + y) * Width + x;
    }
}
