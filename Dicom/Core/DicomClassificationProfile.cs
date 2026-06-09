using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Dicom.Core
{
    // HU 值区间分类配置：按真实 HU(已乘斜率截距)把体素归类到组织类型并赋色
    // 区间表与调色板在加载时一次性导出，分别喂给 Burst Job 与 shader，运行时零重建
    [CreateAssetMenu(fileName = "DicomClassificationProfile", menuName = "Dicom/Classification Profile")]
    public class DicomClassificationProfile : ScriptableObject
    {
        // 单条分类：HU 落在 [HuMin, HuMax) 归为此类，用 Color 着色
        [System.Serializable]
        public struct Category
        {
            public string Name;
            public float HuMin;
            public float HuMax;
            public Color Color;
        }

        // shader 调色板上限，须与 DicomPointCloud.shader 的 _DicomClassColors 数组长度一致
        public const int MaxCategories = 16;

        [SerializeField] List<Category> _categories = new List<Category>();

        public IReadOnlyList<Category> Categories => _categories;
        public int Count => Mathf.Min(_categories.Count, MaxCategories);

        // 按 HU 查类别索引，未命中任何区间返回 -1(shader 端按未分类处理)
        public int ResolveClassId(float hu)
        {
            int n = Count;
            for (int i = 0; i < n; i++)
            {
                var c = _categories[i];
                if (hu >= c.HuMin && hu < c.HuMax) return i;
            }
            return -1;
        }

        // 导出区间下限数组，供 Burst Job 使用(Job 内不能访问托管对象)
        public void GetRanges(out float[] mins, out float[] maxs)
        {
            int n = Count;
            mins = new float[n];
            maxs = new float[n];
            for (int i = 0; i < n; i++)
            {
                mins[i] = _categories[i].HuMin;
                maxs[i] = _categories[i].HuMax;
            }
        }

        // 导出调色板，定长 MaxCategories，未配置项补黑，供 shader SetVectorArray
        public Vector4[] GetPalette()
        {
            var palette = new Vector4[MaxCategories];
            int n = Count;
            for (int i = 0; i < n; i++)
            {
                var col = _categories[i].Color;
                palette[i] = new Vector4(col.r, col.g, col.b, col.a);
            }
            return palette;
        }

        // 用自动识别出的 HU 区间覆盖分类表,颜色按 HSV 均匀分布生成,便于直接区分,之后可在 Inspector 微调
        // mins/maxs 长度须一致,超出 MaxCategories 的部分忽略;名称按区间序号生成
        public void SetCategoriesFromRanges(float[] mins, float[] maxs)
        {
            int n = Mathf.Min(mins.Length, maxs.Length);
            n = Mathf.Min(n, MaxCategories);

            _categories = new List<Category>(n);
            for (int i = 0; i < n; i++)
            {
                float hue = n > 1 ? (float)i / n : 0f;
                _categories.Add(new Category
                {
                    Name = $"区间{i + 1} [{mins[i]:F0},{maxs[i]:F0}]",
                    HuMin = mins[i],
                    HuMax = maxs[i],
                    Color = Color.HSVToRGB(hue, 0.7f, 0.95f)
                });
            }
        }

        // 给一份医学常用 CT 组织分类默认值，便于直接可用(创建资产后可在 Inspector 调)
        public void ResetToCtDefaults()
        {
            _categories = new List<Category>
            {
                new Category { Name = "空气",   HuMin = -1000f, HuMax = -200f, Color = new Color(0.05f, 0.05f, 0.1f, 1f) },
                new Category { Name = "脂肪",   HuMin = -200f,  HuMax = -20f,  Color = new Color(0.95f, 0.85f, 0.4f, 1f) },
                new Category { Name = "软组织", HuMin = -20f,   HuMax = 80f,   Color = new Color(0.85f, 0.4f, 0.4f, 1f) },
                new Category { Name = "血液",   HuMin = 80f,    HuMax = 200f,  Color = new Color(0.6f, 0.1f, 0.1f, 1f) },
                new Category { Name = "骨松质", HuMin = 200f,   HuMax = 700f,  Color = new Color(0.8f, 0.8f, 0.7f, 1f) },
                new Category { Name = "骨皮质", HuMin = 700f,   HuMax = 4000f, Color = new Color(1f, 1f, 0.95f, 1f) }
            };
        }
    }
}
