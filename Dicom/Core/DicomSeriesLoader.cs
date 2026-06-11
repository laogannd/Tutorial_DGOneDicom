using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;

namespace Dicom.Core
{
    // 后台线程扫描目录、解析并按位置排序切片、组装三维体素
    // 全程在线程池执行，禁止触碰 Unity API；完成结果由主线程取回
    public static class DicomSeriesLoader
    {
        public struct Progress
        {
            public DicomLoadPhase Phase;
            public int Done;
            public int Total;
            public string CurrentFile;
        }

        // 在后台线程加载一个 DICOM 目录。progress 回调可能来自非主线程，调用方需自行调度
        public static Task<DicomDataset> LoadDirectoryAsync(string directory, Action<Progress> progress, CancellationToken token)
        {
            return Task.Run(() => LoadDirectory(directory, progress, token), token);
        }

        static DicomDataset LoadDirectory(string directory, Action<Progress> progress, CancellationToken token)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"DICOM 目录不存在: {directory}");

            progress?.Invoke(new Progress { Phase = DicomLoadPhase.Scanning, Done = 0, Total = 0, CurrentFile = directory });

            var files = new List<string>();
            foreach (var f in Directory.EnumerateFiles(directory))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".dcm" || ext == "")
                    files.Add(f);
            }
            if (files.Count == 0)
                throw new InvalidDataFormatException($"目录内无 .dcm 文件: {directory}");

            int total = files.Count;
            var slices = new List<DicomSlice>(total);
            int done = 0;

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(file);
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    var slice = DicomParser.Parse(bytes);
                    slices.Add(slice);
                }
                catch (Exception e)
                {
                    // 精确到出错文件，便于调试面板定位失败切片
                    throw new InvalidDataFormatException($"解析切片失败 [{name}]: {e.Message}");
                }
                done++;
                progress?.Invoke(new Progress { Phase = DicomLoadPhase.Parsing, Done = done, Total = total, CurrentFile = name });
            }

            // 排序与堆叠轴检测:有方向信息则按切片法向投影排序,否则退化到 z / InstanceNumber
            progress?.Invoke(new Progress { Phase = DicomLoadPhase.Sorting, Done = total, Total = total, CurrentFile = "" });

            // 切片法向 = 行余弦 x 列余弦,代表切片堆叠前进方向;整序列取首片方向
            bool hasOrientation = slices[0].HasOrientation;
            float3 normal = hasOrientation
                ? math.normalize(math.cross(slices[0].OrientationRow, slices[0].OrientationCol))
                : new float3(0f, 0f, 1f);

            if (hasOrientation)
                // 按各切片位置在法向上的投影排序,适用于横断/冠状/矢状任意朝向
                slices.Sort((a, b) => math.dot(a.ImagePosition, normal).CompareTo(math.dot(b.ImagePosition, normal)));
            else
            {
                bool hasPosition = slices.Exists(s => s.ImagePositionZ != 0f);
                if (hasPosition)
                    slices.Sort((a, b) => a.ImagePositionZ.CompareTo(b.ImagePositionZ));
                else
                    slices.Sort((a, b) => a.InstanceNumber.CompareTo(b.InstanceNumber));
            }

            progress?.Invoke(new Progress { Phase = DicomLoadPhase.Assembling, Done = total, Total = total, CurrentFile = "" });
            return Assemble(slices, normal, hasOrientation, token);
        }

        // 法向量最接近的患者轴即堆叠轴:0=X(矢状) 1=Y(冠状) 2=Z(横断)
        static int DetectStackAxis(float3 normal)
        {
            float3 a = math.abs(normal);
            if (a.x >= a.y && a.x >= a.z) return 0;
            if (a.y >= a.z) return 1;
            return 2;
        }

        static DicomDataset Assemble(List<DicomSlice> slices, float3 normal, bool hasOrientation, CancellationToken token)
        {
            var first = slices[0];
            int width = first.Columns;
            int height = first.Rows;
            int depth = slices.Count;

            // 校验各切片尺寸一致
            for (int i = 1; i < slices.Count; i++)
                if (slices[i].Columns != width || slices[i].Rows != height)
                    throw new InvalidDataFormatException("切片尺寸不一致，无法组装体积");

            var dataset = new DicomDataset
            {
                Width = width,
                Height = height,
                Depth = depth,
                RescaleSlope = first.RescaleSlope,
                RescaleIntercept = first.RescaleIntercept,
                WindowCenter = first.WindowCenter,
                WindowWidth = first.WindowWidth
            };

            // z 间距：优先用相邻切片沿法向的位置差，否则用层厚
            float zSpacing = first.SliceThickness;
            if (slices.Count > 1)
            {
                float diff = hasOrientation
                    ? math.abs(math.dot(slices[1].ImagePosition - slices[0].ImagePosition, normal))
                    : math.abs(slices[1].ImagePositionZ - slices[0].ImagePositionZ);
                if (diff > 0.0001f) zSpacing = diff;
            }
            dataset.Spacing = new float3(first.PixelSpacingX, first.PixelSpacingY, zSpacing);

            // 检测切片堆叠轴指向的患者轴,供点云按真实重建方向生成(无方向信息默认横断 Z)
            dataset.StackAxis = hasOrientation ? DetectStackAxis(normal) : 2;

            int sliceLen = width * height;
            var voxels = new short[sliceLen * depth];
            for (int z = 0; z < depth; z++)
            {
                token.ThrowIfCancellationRequested();
                Array.Copy(slices[z].Pixels, 0, voxels, z * sliceLen, sliceLen);
            }
            dataset.Voxels = voxels;
            return dataset;
        }
    }
}
