using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dicom.Gene
{
    // mode2 区域分析:后台线程算主导 tag + 遍历全部基因对选中 cell 求均值排 top N
    // 输入 selectedIds/selectedTags 由主线程从掩码收集后传入(纯托管数组,后台安全)
    // 区域名(GeneTagNameTable)是 Unity 对象,须主线程解析,故本类只出 DominantTag,名由调用方补
    public static class GeneRegionAnalyzer
    {
        // selectedIds: 选中 cellId 列表;selectedTags: 对应各 cell 的 tag(与 ids 等长同序)
        public static Task<GeneRegionReport> AnalyzeAsync(int[] selectedIds, int[] selectedTags,
            string exprDir, int cellCount, int topN, Action<float> progress, CancellationToken token)
        {
            return Task.Run(async () =>
            {
                var report = new GeneRegionReport { CellCount = selectedIds.Length };

                report.DominantTag = MajorityTag(selectedTags);
                // 名由主线程补(查 ScriptableObject),此处先给数字回退
                report.RegionName = $"区域{report.DominantTag}";

                // 读全部基因(带进度),对选中 cell 求均值
                var genes = await GeneRepository.LoadAllGenesAsync(exprDir, cellCount, progress, token);

                var scores = new List<GeneRegionReport.GeneScore>(genes.Length);
                foreach (var g in genes)
                {
                    if (g == null) continue;
                    float mean = MeanOverSelection(g.Values, selectedIds);
                    scores.Add(new GeneRegionReport.GeneScore { Gene = g.GeneName, MeanExpression = mean });
                }

                scores.Sort((a, b) => b.MeanExpression.CompareTo(a.MeanExpression));
                int n = Math.Min(topN, scores.Count);
                for (int i = 0; i < n; i++) report.TopGenes.Add(scores[i]);

                return report;
            }, token);
        }

        // 选中 cell 的 tag 直方图取峰值;tag 数值范围小(0..数十),用字典计数
        static int MajorityTag(int[] tags)
        {
            if (tags == null || tags.Length == 0) return 0;
            var counts = new Dictionary<int, int>();
            int best = tags[0];
            int bestCount = 0;
            foreach (int t in tags)
            {
                counts.TryGetValue(t, out int c);
                c++;
                counts[t] = c;
                if (c > bestCount) { bestCount = c; best = t; }
            }
            return best;
        }

        // 对选中 cell 求表达均值,跳过 NaN(缺失 cell);全 NaN 返回 0
        static float MeanOverSelection(float[] values, int[] ids)
        {
            double sum = 0;
            int valid = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (id < 0 || id >= values.Length) continue;
                float v = values[id];
                if (float.IsNaN(v)) continue;
                sum += v;
                valid++;
            }
            return valid > 0 ? (float)(sum / valid) : 0f;
        }
    }
}
