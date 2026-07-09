using UnityEngine;

namespace Dicom.Core
{
    // 离散查找表(LUT)显色配置:用 Gradient 描述伪彩映射,按 Steps 量化成离散色阶
    // 烘焙为 Point 过滤的 1D 纹理(宽=Steps),shader 用窗宽窗位后的归一化强度采样,运行时零重建
    [CreateAssetMenu(fileName = "DicomLutProfile", menuName = "Dicom/LUT Profile")]
    public class DicomLutProfile : ScriptableObject
    {
        // 内置伪彩预设;Custom 时用 _gradient 字段,其余忽略 _gradient 走代码生成的梯度
        public enum LutPreset
        {
            Custom,
            HotIron,    // 热铁:黑->红->橙->黄->白,放射科最常用
            Rainbow,    // 彩虹:蓝->青->绿->黄->红,强调区间差异
            Bone,       // 骨窗:偏暖白灰阶,贴近 CT 骨窗观感
            GrayInverse,// 灰度反相:白->黑,适合暗背景下看高密度
            // 以下为 matplotlib viridis 系列:感知均匀 + 色盲友好,用密集控制点烘焙(见 ViridisColorData)
            Viridis,    // 深紫->蓝->青->绿->黄,viridis 家族基准
            Magma,      // 黑->紫红->橙->浅黄,暗背景高对比
            Plasma,     // 深蓝->品红->橙->黄,鲜艳高饱和
            Inferno,    // 黑->紫红->橙->亮黄,类似 magma 但更亮
            Cividis     // 蓝->灰->黄,专为红绿色盲优化
        }

        // 选择内置预设;改为 Custom 可手动编辑 _gradient
        [SerializeField] LutPreset _preset = LutPreset.HotIron;
        // Custom 模式下生效的源梯度,RGBA 都参与采样
        [SerializeField] Gradient _gradient = BuildGradient(LutPreset.HotIron);
        // 离散级数:把连续梯度切成多少个色阶,医学伪彩常用 8-32
        [SerializeField, Range(2, 256)] int _steps = 16;

        public int Steps => _steps;
        public LutPreset Preset => _preset;

        Texture2D _baked;

        // 烘焙离散 LUT 纹理(宽=Steps,高=1,Point 过滤保证色阶硬边界)
        // 重复调用复用同一纹理对象,仅在尺寸变化时重建,避免频繁 GC
        public Texture2D BakeLut()
        {
            if (_baked == null || _baked.width != _steps)
            {
                if (_baked != null) DestroyBaked();
                _baked = new Texture2D(_steps, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "DicomLut_Baked",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            var pixels = new Color[_steps];

            // viridis 系列:密集控制点数组保真插值,绕开 Gradient 上限 8 个 key 的限制
            var viridis = GetViridisRamp(_preset);
            if (viridis != null)
            {
                for (int i = 0; i < _steps; i++)
                {
                    float t = _steps > 1 ? (i + 0.5f) / _steps : 0f;
                    pixels[i] = SampleRamp(viridis, t);
                }
            }
            else
            {
                // 其余预设走代码生成梯度,Custom 用 Inspector 编辑的 _gradient
                var grad = _preset == LutPreset.Custom ? _gradient : BuildGradient(_preset);
                for (int i = 0; i < _steps; i++)
                {
                    // 取每个色阶中心处的梯度色,避免边界采到相邻阶
                    float t = _steps > 1 ? (i + 0.5f) / _steps : 0f;
                    pixels[i] = grad.Evaluate(t);
                }
            }
            _baked.SetPixels(pixels);
            _baked.Apply(false, false);
            return _baked;
        }

        // 运行时切换预设,下次 BakeLut 生效;调用方需重新 ApplyLut 上传
        public void SetPreset(LutPreset preset) => _preset = preset;

        // 运行时调离散级数,下次 BakeLut 触发纹理重建
        public void SetSteps(int steps) => _steps = Mathf.Clamp(steps, 2, 256);

        // 释放烘焙纹理,Controller 销毁或更换 profile 时调用,防止纹理泄漏
        public void DestroyBaked()
        {
            if (_baked == null) return;
            if (Application.isPlaying) Destroy(_baked);
            else DestroyImmediate(_baked);
            _baked = null;
        }

        // 预设到 viridis 控制点数组的映射;非 viridis 预设返回 null,交回 Gradient 路径
        static ViridisColorData.Rgb[] GetViridisRamp(LutPreset preset)
        {
            switch (preset)
            {
                case LutPreset.Viridis: return ViridisColorData.Viridis;
                case LutPreset.Magma:   return ViridisColorData.Magma;
                case LutPreset.Plasma:  return ViridisColorData.Plasma;
                case LutPreset.Inferno: return ViridisColorData.Inferno;
                case LutPreset.Cividis: return ViridisColorData.Cividis;
                default: return null;
            }
        }

        // 在密集控制点间按归一化位置 t(0..1)线性插值取色;端点 Clamp
        static Color SampleRamp(ViridisColorData.Rgb[] ramp, float t)
        {
            int n = ramp.Length;
            if (n == 1) return ToColor(ramp[0]);

            float x = Mathf.Clamp01(t) * (n - 1);
            int lo = Mathf.FloorToInt(x);
            int hi = Mathf.Min(lo + 1, n - 1);
            float f = x - lo;
            return Color.Lerp(ToColor(ramp[lo]), ToColor(ramp[hi]), f);
        }

        // 8 位 RGB 控制点转 Color(0..1),不透明
        static Color ToColor(ViridisColorData.Rgb c) => new Color(c.R / 255f, c.G / 255f, c.B / 255f, 1f);

        // 按预设构造梯度;关键色 key 不超过 8 个(Gradient 上限)
        static Gradient BuildGradient(LutPreset preset)
        {
            var g = new Gradient();
            GradientColorKey[] keys;
            switch (preset)
            {
                case LutPreset.Rainbow:
                    keys = new[]
                    {
                        new GradientColorKey(new Color(0f, 0f, 1f), 0f),
                        new GradientColorKey(new Color(0f, 1f, 1f), 0.25f),
                        new GradientColorKey(new Color(0f, 1f, 0f), 0.5f),
                        new GradientColorKey(new Color(1f, 1f, 0f), 0.75f),
                        new GradientColorKey(new Color(1f, 0f, 0f), 1f)
                    };
                    break;
                case LutPreset.Bone:
                    keys = new[]
                    {
                        new GradientColorKey(new Color(0f, 0f, 0f), 0f),
                        new GradientColorKey(new Color(0.32f, 0.34f, 0.4f), 0.4f),
                        new GradientColorKey(new Color(0.7f, 0.72f, 0.78f), 0.75f),
                        new GradientColorKey(new Color(1f, 1f, 1f), 1f)
                    };
                    break;
                case LutPreset.GrayInverse:
                    keys = new[]
                    {
                        new GradientColorKey(new Color(1f, 1f, 1f), 0f),
                        new GradientColorKey(new Color(0f, 0f, 0f), 1f)
                    };
                    break;
                case LutPreset.HotIron:
                default:
                    keys = new[]
                    {
                        new GradientColorKey(new Color(0f, 0f, 0f), 0f),
                        new GradientColorKey(new Color(0.6f, 0f, 0f), 0.25f),
                        new GradientColorKey(new Color(1f, 0.5f, 0f), 0.5f),
                        new GradientColorKey(new Color(1f, 1f, 0f), 0.75f),
                        new GradientColorKey(new Color(1f, 1f, 1f), 1f)
                    };
                    break;
            }
            g.SetKeys(keys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }
    }
}
