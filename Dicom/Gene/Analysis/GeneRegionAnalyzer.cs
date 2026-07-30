using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dicom.Gene
{
    // mode2 区域分析:后台线程算主导 tag + 遍历全部基因对选中 cell 求均值排 top N
    // 输入 selectedIds/selectedTags 由主线程从掩码收集后传入(纯托管数组,后台安全)
    // 区域名(GeneTagNameTable)是 Unity 对象,须主线程解析,故本类只出 DominantTag,名由调用方补
    // 药物(mode3):drug 快照是不可变纯托管对象,后台线程按它逐 cell 变换表达值后再统计,
    //   故画笔分析结果就是"药物作用后的反应";快照 Revision 回填到报告,调用方据此判定结果是否已过期
    public static class GeneRegionAnalyzer
    {
        // selectedIds: 选中 cellId 列表;selectedTags: 对应各 cell 的 tag(与 ids 等长同序)
        // allTags: 全模型逐 cell 的 tag(按 cellId 索引),供靶区域药物变换全模型分母;可为 null
        // drug: 药物快照,null 视为无药基线
        public static Task<GeneRegionReport> AnalyzeAsync(int[] selectedIds, int[] selectedTags,
            string exprDir, int cellCount, int topN, GeneDrugSnapshot drug, int[] allTags,
            Action<float> progress, CancellationToken token)
        {
            var snapshot = drug ?? GeneDrugSnapshot.None;

            return Task.Run(async () =>
            {
                var report = new GeneRegionReport
                {
                    CellCount = selectedIds.Length,
                    DrugRevision = snapshot.Revision,
                    DrugName = snapshot.DrugName,
                    DrugDose = snapshot.Dose
                };

                report.DominantTag = MajorityTag(selectedTags);
                // 名由主线程补(查 ScriptableObject),此处先给数字回退
                report.RegionName = $"区域{report.DominantTag}";

                // 读全部基因(带进度),对选中 cell 求均值
                var genes = await GeneRepository.LoadAllGenesAsync(exprDir, cellCount, progress, token);

                var scores = new List<GeneRegionReport.GeneScore>(genes.Length);
                // 药物变换缓冲:逐基因复用,避免每个基因都分配一条 13.6 万长数组
                float[] buffer = null;

                foreach (var g in genes)
                {
                    if (g == null) continue;
                    token.ThrowIfCancellationRequested();

                    // 药后表达值(无药或该药不作用于此基因时直接用基线数组,零拷贝)
                    float[] values = g.Values;
                    if (snapshot.HasEffect && snapshot.AffectsGene(g.GeneName))
                    {
                        if (buffer == null || buffer.Length != values.Length) buffer = new float[values.Length];
                        snapshot.ApplyAll(values, buffer, g.GeneName, allTags);
                        values = buffer;
                    }

                    float mean = MeanOverSelection(values, selectedIds);
                    scores.Add(new GeneRegionReport.GeneScore { Gene = g.GeneName, MeanExpression = mean });

                    // 画取比例 = 选区内表达该基因(v>0)的 cell 数 / 全模型表达该基因的 cell 数
                    // 分子分母都用药后值(药物可能把弱表达压到 0 或激活出新表达),故须重数全模型
                    int expressedInSel = CountExpressed(values, selectedIds);
                    int expressedTotal = ReferenceEquals(values, g.Values)
                        ? g.ExpressedCount
                        : CountExpressedAll(values);
                    // 全模型无表达(分母 0)记 -1,搜索时显示"无表达"
                    report.PaintFractions[g.GeneName] = expressedTotal > 0
                        ? (float)expressedInSel / expressedTotal
                        : -1f;
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

        // 数选区内表达该基因(值非 NaN 且 > 0)的 cell 数,供画取比例分子
        static int CountExpressed(float[] values, int[] ids)
        {
            int n = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (id < 0 || id >= values.Length) continue;
                float v = values[id];
                if (!float.IsNaN(v) && v > 0f) n++;
            }
            return n;
        }

        // 数全模型表达该基因的 cell 数(药后值,分母随药物变化故须重数)
        static int CountExpressedAll(float[] values)
        {
            int n = 0;
            for (int i = 0; i < values.Length; i++)
            {
                float v = values[i];
                if (!float.IsNaN(v) && v > 0f) n++;
            }
            return n;
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
