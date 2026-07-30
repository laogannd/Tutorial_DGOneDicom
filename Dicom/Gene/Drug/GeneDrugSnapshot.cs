namespace Dicom.Gene
{
    // 药物状态的不可变快照:药物模块与显色/分析模块之间唯一的传递单元
    // 纯托管、只读、无 Unity API,故可安全跨线程(区域分析在后台线程按同一快照变换表达值)
    // Revision 由 GeneDrugController 单调递增:异步分析完成后比对快照版本,不一致即判定结果已过期
    public sealed class GeneDrugSnapshot
    {
        // 无药物基线快照,Revision=0
        public static readonly GeneDrugSnapshot None = new GeneDrugSnapshot(null, 0f, 0f, 0);

        readonly GeneDrugDefinition _def;

        // 药名(无药为空串)、当前剂量、剂量归一化后的效应强度 0..1、状态版本号
        public readonly string DrugName;
        public readonly float Dose;
        public readonly float Effect;
        public readonly int Revision;

        public GeneDrugSnapshot(GeneDrugDefinition def, float dose, float effect, int revision)
        {
            _def = def;
            DrugName = def != null ? def.Name : "";
            Dose = dose;
            Effect = effect;
            Revision = revision;
        }

        // 是否真正改变表达值:有药且效应大于 0
        public bool HasEffect => _def != null && Effect > 0f;

        // 该药是否作用于此基因(靶基因列表为空=作用于全部基因)
        public bool AffectsGene(string geneName) => _def != null && _def.MatchesGene(geneName);

        // 单 cell 变换:NaN(无该 cell 数据)透传;结果下限 0
        // scale 从 1 平滑插值到满剂量总倍率,故剂量连续变化即得表达强度连续过渡
        public float Apply(float value, string geneName, int tag)
        {
            if (!HasEffect) return value;
            if (float.IsNaN(value)) return value;
            if (!_def.MatchesGene(geneName)) return value;

            float target = _def.GlobalScale * _def.TargetGeneScale * TagScale(tag);
            float scale = 1f + (target - 1f) * Effect;
            float v = value * scale + _def.Bias * Effect;
            return v < 0f ? 0f : v;
        }

        // 靶区域倍率:未指定靶区域=全区域 1;指定则仅命中的 tag 叠加倍率
        float TagScale(int tag)
        {
            var tags = _def.TargetTags;
            if (tags == null || tags.Length == 0) return 1f;
            for (int i = 0; i < tags.Length; i++)
                if (tags[i] == tag) return _def.TargetTagScale;
            return 1f;
        }

        // 批量变换整条基因表达数组到 dst(dst 由调用方复用,长度须与 src 一致)
        // tags 可为 null(该药未指定靶区域时用不到);无效应时直接整段复制
        public void ApplyAll(float[] src, float[] dst, string geneName, int[] tags)
        {
            if (src == null || dst == null) return;
            int n = src.Length < dst.Length ? src.Length : dst.Length;

            if (!HasEffect || !AffectsGene(geneName))
            {
                System.Array.Copy(src, dst, n);
                return;
            }

            bool needTag = _def.TargetTags != null && _def.TargetTags.Length > 0 && tags != null;
            for (int i = 0; i < n; i++)
                dst[i] = Apply(src[i], geneName, needTag ? tags[i] : 0);
        }

        public string DescribeShort()
        {
            if (!HasEffect) return "无药物";
            return $"{DrugName} 剂量 {Dose:F2}";
        }
    }
}
