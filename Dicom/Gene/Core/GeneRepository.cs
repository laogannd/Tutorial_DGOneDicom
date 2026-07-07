using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Unity.Mathematics;

namespace Dicom.Gene
{
    // 基因数据仓库:后台流式解析 cell_mapping.json 与 expression/*.json
    // 用 JsonTextReader 逐 token 读,cellId 连续直接按 int 索引写定长数组,不建整树避免 13.6 万条目 GC
    // 全部方法在后台线程执行,禁止触碰 Unity API;无 static 可变状态(缓存放调用方实例)
    public static class GeneRepository
    {
        public const string CellMappingFileName = "cell_mapping.json";
        public const string ExpressionDirName = "expression";

        // 扫 expression 目录列出基因名(文件名去扩展名),纯 IO,主线程可调
        // 目录不存在返回空数组;按名称排序保证下拉菜单稳定
        public static string[] ListGenes(string exprDir)
        {
            if (!Directory.Exists(exprDir)) return Array.Empty<string>();

            var names = new List<string>();
            foreach (var f in Directory.EnumerateFiles(exprDir, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        // 后台解析 cell_mapping.json;progress 回调可能来自非主线程,调用方需自行调度
        public static Task<GeneModelData> LoadCellMappingAsync(string path, Action<float> progress, CancellationToken token)
        {
            return Task.Run(() => ParseCellMapping(path, progress, token), token);
        }

        // 后台解析单个基因表达文件;cellCount 来自已加载的 GeneModelData,用于分配定长数组
        public static Task<GeneExpression> LoadGeneAsync(string exprDir, string geneName, int cellCount, CancellationToken token)
        {
            return Task.Run(() => ParseExpression(Path.Combine(exprDir, geneName + ".json"), cellCount, token), token);
        }

        // 后台并行解析全部基因(mode2 top5 用);progress 报已完成基因数比例
        public static async Task<GeneExpression[]> LoadAllGenesAsync(string exprDir, int cellCount,
            Action<float> progress, CancellationToken token)
        {
            var names = ListGenes(exprDir);
            var results = new GeneExpression[names.Length];
            int done = 0;

            // 限制并发度防止一次性打开过多文件流(IO 才是瓶颈,4 路足够)
            using (var gate = new SemaphoreSlim(4))
            {
                var tasks = new Task[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    int idx = i;
                    tasks[i] = Task.Run(async () =>
                    {
                        await gate.WaitAsync(token);
                        try
                        {
                            results[idx] = ParseExpression(Path.Combine(exprDir, names[idx] + ".json"), cellCount, token);
                            int d = Interlocked.Increment(ref done);
                            progress?.Invoke((float)d / names.Length);
                        }
                        finally { gate.Release(); }
                    }, token);
                }
                await Task.WhenAll(tasks);
            }
            return results;
        }

        // 流式解析 cell_mapping:外层每个 property name = cellId,值对象含 x/y/z/tag
        // cellId 连续但不保证升序出现,先收进临时列表按最大 id+1 定长回填
        static GeneModelData ParseCellMapping(string path, Action<float> progress, CancellationToken token)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"cell_mapping 不存在: {path}");

            // 先扫一遍收集,cellId 连续可直接按最大值定长;用列表暂存避免二次读文件
            var ids = new List<int>(140000);
            var grids = new List<int3>(140000);
            var tags = new List<int>(140000);

            long fileLength = new FileInfo(path).Length;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var sr = new StreamReader(stream))
            using (var reader = new JsonTextReader(sr))
            {
                // 期望根为对象
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("cell_mapping 根节点应为 JSON 对象");

                int sinceProgress = 0;
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    token.ThrowIfCancellationRequested();

                    int cellId = int.Parse((string)reader.Value);
                    ReadCellObject(reader, out int x, out int y, out int z, out int tag);

                    ids.Add(cellId);
                    grids.Add(new int3(x, y, z));
                    tags.Add(tag);

                    // 每 8192 条按文件读取位置估算进度,避免频繁回调
                    if (++sinceProgress >= 8192)
                    {
                        sinceProgress = 0;
                        progress?.Invoke(Mathf01(stream.Position, fileLength));
                    }
                }
            }

            int count = 0;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] + 1 > count) count = ids[i] + 1;

            var data = new GeneModelData
            {
                CellCount = count,
                Grid = new int3[count],
                Tag = new int[count]
            };

            int3 gmin = new int3(int.MaxValue);
            int3 gmax = new int3(int.MinValue);
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                data.Grid[id] = grids[i];
                data.Tag[id] = tags[i];
                gmin = math.min(gmin, grids[i]);
                gmax = math.max(gmax, grids[i]);
            }
            data.GridMin = gmin;
            data.GridMax = gmax;

            progress?.Invoke(1f);
            return data;
        }

        // 读一个 cell 值对象 { "x":..,"y":..,"z":..,"tag":".." };tag 可能是字符串或数字
        static void ReadCellObject(JsonTextReader reader, out int x, out int y, out int z, out int tag)
        {
            x = y = z = tag = 0;
            if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                throw new InvalidDataException("cell 值应为对象");

            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                string prop = (string)reader.Value;
                reader.Read();
                switch (prop)
                {
                    case "x": x = Convert.ToInt32(reader.Value); break;
                    case "y": y = Convert.ToInt32(reader.Value); break;
                    case "z": z = Convert.ToInt32(reader.Value); break;
                    case "tag": tag = ParseTag(reader.Value); break;
                }
            }
        }

        // tag 字段兼容字符串("7")与数字(7),非法则归 0
        static int ParseTag(object value)
        {
            if (value == null) return 0;
            if (value is long l) return (int)l;
            if (value is string s) return int.TryParse(s, out int t) ? t : 0;
            return Convert.ToInt32(value);
        }

        // 流式解析 expression:{ "gene":.., "cell_count":.., "expression": { "0":val, .. } }
        // Values 定长 cellCount,缺失 cell 填 NaN;同时统计有效值 Min/Max
        static GeneExpression ParseExpression(string path, int cellCount, CancellationToken token)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"基因表达文件不存在: {path}");

            var values = new float[cellCount];
            for (int i = 0; i < cellCount; i++) values[i] = float.NaN;

            string geneName = Path.GetFileNameWithoutExtension(path);
            float min = float.MaxValue;
            float max = float.MinValue;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var sr = new StreamReader(stream))
            using (var reader = new JsonTextReader(sr))
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("expression 根节点应为 JSON 对象");

                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    token.ThrowIfCancellationRequested();
                    string prop = (string)reader.Value;

                    if (prop == "gene")
                    {
                        reader.Read();
                        geneName = (string)reader.Value;
                    }
                    else if (prop == "expression")
                    {
                        if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                            throw new InvalidDataException("expression 字段应为对象");

                        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                        {
                            int cellId = int.Parse((string)reader.Value);
                            reader.Read();
                            float v = Convert.ToSingle(reader.Value);
                            if (cellId >= 0 && cellId < cellCount)
                            {
                                values[cellId] = v;
                                if (v < min) min = v;
                                if (v > max) max = v;
                            }
                        }
                    }
                    else
                    {
                        // 跳过其他标量字段(如 cell_count)
                        reader.Read();
                    }
                }
            }

            // 全空文件兜底,避免归一化除零
            if (min > max) { min = 0f; max = 1f; }

            return new GeneExpression { GeneName = geneName, Values = values, Min = min, Max = max };
        }

        // 文件位置比例,clamp 到 0..1(不引 UnityEngine.Mathf,后台线程纯 C#)
        static float Mathf01(long pos, long len)
        {
            if (len <= 0) return 0f;
            float r = (float)pos / len;
            return r < 0f ? 0f : (r > 1f ? 1f : r);
        }
    }
}
