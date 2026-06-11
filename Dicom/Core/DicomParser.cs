using System;
using System.Text;
using Unity.Mathematics;

namespace Dicom.Core
{
    // 单张 DICOM 切片的解析结果，序列加载器据此组装三维体素
    public struct DicomSlice
    {
        public int Rows;
        public int Columns;
        public float PixelSpacingX;
        public float PixelSpacingY;
        public float SliceThickness;
        // ImagePositionPatient(0020,0032) 切片左上角患者坐标(mm)，三轴完整保留供按法向排序
        public float3 ImagePosition;
        // ImageOrientationPatient(0020,0037) 行/列方向余弦，叉乘得切片法向(堆叠方向)
        public float3 OrientationRow;
        public float3 OrientationCol;
        public bool HasOrientation;
        public int InstanceNumber;
        public float RescaleSlope;
        public float RescaleIntercept;
        public float WindowCenter;
        public float WindowWidth;
        public int BitsAllocated;
        public bool PixelRepresentationSigned;
        public short[] Pixels;

        // z 分量兼容旧调用(横断面排序回退用)
        public float ImagePositionZ => ImagePosition.z;
    }

    // 精简 DICOM 解析器：只读必要 Tag 与非压缩 16bit 像素，无第三方依赖，IL2CPP/Android 安全
    public static class DicomParser
    {
        // 隐式 VR Little Endian 传输语法 UID
        const string ImplicitVRLE = "1.2.840.10008.1.2";

        public static DicomSlice Parse(byte[] data)
        {
            if (data == null || data.Length < 132)
                throw new InvalidDataFormatException("DICOM 数据为空或过短");

            // 校验 128 字节前导 + "DICM" 魔数
            if (!(data[128] == 'D' && data[129] == 'I' && data[130] == 'C' && data[131] == 'M'))
                throw new InvalidDataFormatException("缺少 DICM 魔数，非标准 DICOM Part10 文件");

            var slice = new DicomSlice
            {
                RescaleSlope = 1f,
                RescaleIntercept = 0f,
                SliceThickness = 1f,
                PixelSpacingX = 1f,
                PixelSpacingY = 1f,
                BitsAllocated = 16
            };

            ReadElements(data, ref slice);
            ValidateAndConvert(ref slice);
            return slice;
        }

        // 像素数据元素读取后暂存的原始字节与偏移
        static byte[] _pixelBytes;

        static void ReadElements(byte[] data, ref DicomSlice slice)
        {
            _pixelBytes = null;
            int pos = 132;

            // File Meta(0002 组)恒为显式 VR Little Endian
            string transferSyntax = ReadFileMeta(data, ref pos);
            bool implicitVR = transferSyntax == ImplicitVRLE;

            // 解析主数据集
            while (pos + 8 <= data.Length)
            {
                ushort group = (ushort)(data[pos] | (data[pos + 1] << 8));
                ushort element = (ushort)(data[pos + 2] | (data[pos + 3] << 8));
                pos += 4;

                int length;
                string vr = null;

                if (implicitVR)
                {
                    length = ReadInt32(data, pos);
                    pos += 4;
                }
                else
                {
                    vr = Encoding.ASCII.GetString(data, pos, 2);
                    pos += 2;
                    // OB/OW/OF/SQ/UT/UN 使用 2 字节保留 + 4 字节长度
                    if (vr == "OB" || vr == "OW" || vr == "OF" || vr == "SQ" || vr == "UT" || vr == "UN")
                    {
                        pos += 2;
                        length = ReadInt32(data, pos);
                        pos += 4;
                    }
                    else
                    {
                        length = data[pos] | (data[pos + 1] << 8);
                        pos += 2;
                    }
                }

                // 像素数据 (7FE0,0010)
                if (group == 0x7FE0 && element == 0x0010)
                {
                    if (length < 0 || length == -1 || (uint)length == 0xFFFFFFFF)
                        throw new InvalidDataFormatException("像素数据为压缩/封装格式，当前解析器仅支持非压缩 16bit");
                    _pixelBytes = new byte[length];
                    Array.Copy(data, pos, _pixelBytes, 0, length);
                    return;
                }

                if (length < 0 || pos + length > data.Length)
                    break;

                AssignTag(group, element, data, pos, length, ref slice);
                pos += length;
            }
        }

        // 读取 File Meta(0002,xxxx)，返回传输语法 UID，pos 移动到主数据集起点
        static string ReadFileMeta(byte[] data, ref int pos)
        {
            string transferSyntax = ImplicitVRLE;
            while (pos + 8 <= data.Length)
            {
                ushort group = (ushort)(data[pos] | (data[pos + 1] << 8));
                if (group != 0x0002)
                    break;

                ushort element = (ushort)(data[pos + 2] | (data[pos + 3] << 8));
                pos += 4;

                string vr = Encoding.ASCII.GetString(data, pos, 2);
                pos += 2;
                int length;
                if (vr == "OB" || vr == "OW" || vr == "OF" || vr == "SQ" || vr == "UT" || vr == "UN")
                {
                    pos += 2;
                    length = ReadInt32(data, pos);
                    pos += 4;
                }
                else
                {
                    length = data[pos] | (data[pos + 1] << 8);
                    pos += 2;
                }

                // (0002,0010) Transfer Syntax UID
                if (element == 0x0010)
                    transferSyntax = Encoding.ASCII.GetString(data, pos, length).Trim('\0', ' ');

                pos += length;
            }
            return transferSyntax;
        }

        static void AssignTag(ushort group, ushort element, byte[] data, int pos, int length, ref DicomSlice slice)
        {
            if (group == 0x0028)
            {
                switch (element)
                {
                    case 0x0010: slice.Rows = ReadUShort(data, pos); break;
                    case 0x0011: slice.Columns = ReadUShort(data, pos); break;
                    case 0x0100: slice.BitsAllocated = ReadUShort(data, pos); break;
                    case 0x0103: slice.PixelRepresentationSigned = ReadUShort(data, pos) == 1; break;
                    case 0x0030: // PixelSpacing: 两个 DS，反斜杠分隔(row\col)
                        var ps = SplitDecimals(data, pos, length);
                        if (ps.Length >= 2) { slice.PixelSpacingY = ps[0]; slice.PixelSpacingX = ps[1]; }
                        else if (ps.Length == 1) { slice.PixelSpacingX = slice.PixelSpacingY = ps[0]; }
                        break;
                    case 0x1050: slice.WindowCenter = FirstDecimal(data, pos, length, 40f); break;
                    case 0x1051: slice.WindowWidth = FirstDecimal(data, pos, length, 400f); break;
                    case 0x1052: slice.RescaleIntercept = FirstDecimal(data, pos, length, 0f); break;
                    case 0x1053: slice.RescaleSlope = FirstDecimal(data, pos, length, 1f); break;
                }
            }
            else if (group == 0x0018 && element == 0x0050)
            {
                slice.SliceThickness = FirstDecimal(data, pos, length, 1f);
            }
            else if (group == 0x0020)
            {
                if (element == 0x0013) // InstanceNumber (IS)
                    slice.InstanceNumber = (int)FirstDecimal(data, pos, length, 0f);
                else if (element == 0x0032) // ImagePositionPatient: x\y\z
                {
                    var ipp = SplitDecimals(data, pos, length);
                    if (ipp.Length >= 3) slice.ImagePosition = new float3(ipp[0], ipp[1], ipp[2]);
                }
                else if (element == 0x0037) // ImageOrientationPatient: 行余弦(3) \ 列余弦(3)
                {
                    var iop = SplitDecimals(data, pos, length);
                    if (iop.Length >= 6)
                    {
                        slice.OrientationRow = new float3(iop[0], iop[1], iop[2]);
                        slice.OrientationCol = new float3(iop[3], iop[4], iop[5]);
                        slice.HasOrientation = true;
                    }
                }
            }
        }

        static void ValidateAndConvert(ref DicomSlice slice)
        {
            if (slice.Rows <= 0 || slice.Columns <= 0)
                throw new InvalidDataFormatException("缺少有效的 Rows/Columns");
            if (_pixelBytes == null)
                throw new InvalidDataFormatException("未找到像素数据 (7FE0,0010)");
            if (slice.BitsAllocated != 16)
                throw new InvalidDataFormatException($"仅支持 16bit 像素，实际 BitsAllocated={slice.BitsAllocated}");

            int count = slice.Rows * slice.Columns;
            if (_pixelBytes.Length < count * 2)
                throw new InvalidDataFormatException("像素数据长度小于 Rows*Columns*2");

            var pixels = new short[count];
            // 小端 16bit，无论有无符号都按 short 存(HU 范围足够)
            for (int i = 0; i < count; i++)
                pixels[i] = (short)(_pixelBytes[i * 2] | (_pixelBytes[i * 2 + 1] << 8));

            slice.Pixels = pixels;
            _pixelBytes = null;
        }

        static ushort ReadUShort(byte[] d, int p) => (ushort)(d[p] | (d[p + 1] << 8));
        static int ReadInt32(byte[] d, int p) => d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);

        // 解析 DS/IS 多值字符串(反斜杠分隔)为 float 数组
        static float[] SplitDecimals(byte[] d, int p, int len)
        {
            string s = Encoding.ASCII.GetString(d, p, len).Trim('\0', ' ');
            if (s.Length == 0) return Array.Empty<float>();
            var parts = s.Split('\\');
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out result[i]);
            return result;
        }

        // 取多值的第一个，解析失败用 fallback(外部数据来源，允许回退)
        static float FirstDecimal(byte[] d, int p, int len, float fallback)
        {
            var vals = SplitDecimals(d, p, len);
            return vals.Length > 0 ? vals[0] : fallback;
        }
    }

    public class InvalidDataFormatException : Exception
    {
        public InvalidDataFormatException(string message) : base(message) { }
    }
}
