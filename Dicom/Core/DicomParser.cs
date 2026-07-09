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
        // BitsStored(0028,0101):有效位数,0 表示未提供(信任等于 BitsAllocated)
        public int BitsStored;
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
        // 显式 VR Little Endian 传输语法 UID
        const string ExplicitVRLE = "1.2.840.10008.1.2.1";

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

            byte[] pixelBytes = ReadElements(data, ref slice);
            ValidateAndConvert(ref slice, pixelBytes);
            return slice;
        }

        // 解析主数据集所有元素并回填 slice，返回像素数据原始字节(未找到返回 null)
        // 无 static 状态，可重入/多线程安全；对损坏或恶意文件的越界长度做边界校验后中止解析
        static byte[] ReadElements(byte[] data, ref DicomSlice slice)
        {
            int pos = 132;

            // File Meta(0002 组)恒为显式 VR Little Endian
            string transferSyntax = ReadFileMeta(data, ref pos);
            // 仅支持隐式/显式 VR Little Endian;大端或压缩等其它语法会被按小端错解成垃圾值,
            // 必须显式拒绝而非静默产出错误体数据(快速失败)
            if (transferSyntax != ImplicitVRLE && transferSyntax != ExplicitVRLE)
                throw new InvalidDataFormatException($"不支持的传输语法: {transferSyntax}(仅支持隐式/显式 VR Little Endian 非压缩)");
            bool implicitVR = transferSyntax == ImplicitVRLE;

            // 解析主数据集
            while (pos + 8 <= data.Length)
            {
                ushort group = (ushort)(data[pos] | (data[pos + 1] << 8));
                ushort element = (ushort)(data[pos + 2] | (data[pos + 3] << 8));
                pos += 4;

                // 无符号读取长度:0xFFFFFFFF 为未定义长度(序列/SQ),需与真正越界区分
                uint rawLength;
                bool isSQ = false;
                string vr = null;

                if (implicitVR)
                {
                    rawLength = ReadUInt32(data, pos);
                    pos += 4;
                }
                else
                {
                    vr = Encoding.ASCII.GetString(data, pos, 2);
                    pos += 2;
                    isSQ = vr == "SQ";
                    // OB/OW/OF/SQ/UT/UN 使用 2 字节保留 + 4 字节长度
                    if (vr == "OB" || vr == "OW" || vr == "OF" || vr == "SQ" || vr == "UT" || vr == "UN")
                    {
                        // 读 4 字节长度前先确认保留位+长度位在界内，防越界读
                        if (pos + 6 > data.Length) break;
                        pos += 2;
                        rawLength = ReadUInt32(data, pos);
                        pos += 4;
                    }
                    else
                    {
                        rawLength = (uint)(data[pos] | (data[pos + 1] << 8));
                        pos += 2;
                    }
                }

                // 像素数据 (7FE0,0010)
                if (group == 0x7FE0 && element == 0x0010)
                {
                    if (rawLength == 0xFFFFFFFF)
                        throw new InvalidDataFormatException("像素数据为压缩/封装格式，当前解析器仅支持非压缩 16bit");
                    // 声明长度超过剩余字节即为损坏文件，拒绝分配防越界拷贝
                    if (pos + (long)rawLength > data.Length)
                        throw new InvalidDataFormatException("像素数据长度越界，文件损坏或被截断");
                    int pixelLen = (int)rawLength;
                    var pixelBytes = new byte[pixelLen];
                    Array.Copy(data, pos, pixelBytes, 0, pixelLen);
                    return pixelBytes;
                }

                // 未定义长度(0xFFFFFFFF):隐式 VR 的序列或显式 VR 的 SQ。
                // 这类元素常出现在 PixelData 之前(如 Referenced Image Sequence),
                // 早期实现当作损坏文件 break 会丢失后续像素数据,这里跳过整个序列后继续
                if (rawLength == 0xFFFFFFFF && (implicitVR || isSQ))
                {
                    if (!SkipUndefinedLengthSequence(data, ref pos)) break;
                    continue;
                }

                // 定义长度的 SQ:整体跳过其内容(不解析嵌套项),继续读后续顶层元素
                if (isSQ)
                {
                    if (pos + (long)rawLength > data.Length) break;
                    pos += (int)rawLength;
                    continue;
                }

                // 长度越界或为未定义长度(非序列语义):文件损坏,停止解析(已读 Tag 交由 ValidateAndConvert 校验)
                if (rawLength == 0xFFFFFFFF || pos + (long)rawLength > data.Length)
                    break;

                int length = (int)rawLength;
                AssignTag(group, element, data, pos, length, ref slice);
                pos += length;
            }
            return null;
        }

        // 跳过一个未定义长度序列:从当前 pos 起按 item 定界符扫描,直到序列结束定界符(FFFE,E0DD)。
        // 支持嵌套序列(item 内可再含未定义长度 SQ)。成功推进 pos 到序列结束后返回 true;数据不足返回 false
        static bool SkipUndefinedLengthSequence(byte[] data, ref int pos)
        {
            while (pos + 8 <= data.Length)
            {
                ushort group = (ushort)(data[pos] | (data[pos + 1] << 8));
                ushort element = (ushort)(data[pos + 2] | (data[pos + 3] << 8));
                uint itemLen = ReadUInt32(data, pos + 4);
                pos += 8;

                if (group != 0xFFFE) return false; // 非定界符结构,视为损坏

                if (element == 0xE0DD) return true;        // 序列结束定界符 (FFFE,E0DD)
                if (element == 0xE00D) continue;           // 项结束定界符 (FFFE,E00D)
                if (element == 0xE000)                     // 项开始 (FFFE,E000)
                {
                    if (itemLen == 0xFFFFFFFF)
                    {
                        // 未定义长度项:递归扫描到项结束定界符
                        if (!SkipUndefinedLengthItem(data, ref pos)) return false;
                    }
                    else
                    {
                        if (pos + (long)itemLen > data.Length) return false;
                        pos += (int)itemLen;
                    }
                    continue;
                }
                return false;
            }
            return false;
        }

        // 跳过一个未定义长度项:扫描到项结束定界符(FFFE,E00D),其内可含定义长度元素
        static bool SkipUndefinedLengthItem(byte[] data, ref int pos)
        {
            while (pos + 8 <= data.Length)
            {
                ushort group = (ushort)(data[pos] | (data[pos + 1] << 8));
                ushort element = (ushort)(data[pos + 2] | (data[pos + 3] << 8));
                uint len = ReadUInt32(data, pos + 4);
                pos += 8;

                if (group == 0xFFFE && element == 0xE00D) return true; // 项结束定界符
                if (len == 0xFFFFFFFF)
                {
                    // 内嵌未定义长度序列,递归跳过
                    if (!SkipUndefinedLengthSequence(data, ref pos)) return false;
                    continue;
                }
                if (pos + (long)len > data.Length) return false;
                pos += (int)len;
            }
            return false;
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
                    if (pos + 6 > data.Length) break;
                    pos += 2;
                    length = ReadInt32(data, pos);
                    pos += 4;
                }
                else
                {
                    length = data[pos] | (data[pos + 1] << 8);
                    pos += 2;
                }

                // 长度越界即中止 File Meta 解析，防越界读
                if (length < 0 || pos + length > data.Length)
                    break;

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
                    case 0x0101: slice.BitsStored = ReadUShort(data, pos); break;
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

        static void ValidateAndConvert(ref DicomSlice slice, byte[] pixelBytes)
        {
            if (slice.Rows <= 0 || slice.Columns <= 0)
                throw new InvalidDataFormatException("缺少有效的 Rows/Columns");
            if (pixelBytes == null)
                throw new InvalidDataFormatException("未找到像素数据 (7FE0,0010)");
            if (slice.BitsAllocated != 16)
                throw new InvalidDataFormatException($"仅支持 16bit 像素，实际 BitsAllocated={slice.BitsAllocated}");

            // 用 long 计算避免 Rows*Columns 及 *2 在恶意/极端尺寸下整型溢出绕过校验
            long countL = (long)slice.Rows * slice.Columns;
            if (countL <= 0 || countL > int.MaxValue)
                throw new InvalidDataFormatException($"像素总数非法或过大: Rows={slice.Rows} Columns={slice.Columns}");
            int count = (int)countL;
            if (pixelBytes.Length < countL * 2)
                throw new InvalidDataFormatException("像素数据长度小于 Rows*Columns*2");

            // 有效位数:未提供时信任等于分配位数
            int bitsStored = slice.BitsStored > 0 && slice.BitsStored <= 16 ? slice.BitsStored : 16;
            int mask = bitsStored >= 16 ? 0xFFFF : (1 << bitsStored) - 1;
            int signBit = 1 << (bitsStored - 1);
            bool signed = slice.PixelRepresentationSigned;

            var pixels = new short[count];
            for (int i = 0; i < count; i++)
            {
                // 小端 16bit,按 BitsStored 掩掉高位填充,消除高位噪声(常见 12bit 存于 16bit)
                int raw = (pixelBytes[i * 2] | (pixelBytes[i * 2 + 1] << 8)) & mask;
                // 有符号数据按有效位做符号扩展,得到正确负值
                if (signed && (raw & signBit) != 0)
                    raw -= (mask + 1);
                pixels[i] = (short)raw;
            }

            slice.Pixels = pixels;
        }

        static ushort ReadUShort(byte[] d, int p) => (ushort)(d[p] | (d[p + 1] << 8));
        static int ReadInt32(byte[] d, int p) => d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);
        static uint ReadUInt32(byte[] d, int p) => (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));

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
