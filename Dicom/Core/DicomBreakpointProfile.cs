using System.Collections.Generic;
using UnityEngine;

namespace Dicom.Core
{
    // 断点插值显色配置:用户给若干个真实测量值断点(与阈值/归一化滑块同一数值空间),每个断点配一个颜色
    // 例如 -700/-500/-300/-100/100/300/500/700 各对应一色,两断点之间线性插值过渡
    // 烘焙为 Bilinear 过滤的 1D 纹理,shader 把点的真实值映射到断点值域作 u 坐标采样,运行时零重建
    [CreateAssetMenu(fileName = "DicomBreakpointProfile", menuName = "Dicom/Breakpoint Profile")]
    public class DicomBreakpointProfile : ScriptableObject
    {
        // 单个断点:真实值 Value 处显示 Color,相邻断点间线性插值
        [System.Serializable]
        public struct Stop
        {
            public float Value;
            public Color Color;
        }

        // 烘焙纹理宽度:断点值域细分采样数,256 足够平滑且显存极小
        const int BakeWidth = 256;

        // 断点列表,按 Value 升序使用;Inspector 配置后自动排序烘焙
        [SerializeField] List<Stop> _stops = new List<Stop>();

        public IReadOnlyList<Stop> Stops => _stops;
        public int Count => _stops.Count;

        // 断点值域下限/上限,即首尾断点的 Value,供 shader 把真实值归一化到 0..1 采样坐标
        public float DomainMin => _stops.Count > 0 ? _stops[0].Value : 0f;
        public float DomainMax => _stops.Count > 0 ? _stops[_stops.Count - 1].Value : 1f;

        Texture2D _baked;

        // 按 Value 升序排序后烘焙;调用方应先 SortStops 或在编辑器内保证有序
        // 烘焙 BakeWidth 宽 1D 纹理,逐纹素按分段线性插值取色(正确处理不等距断点)
        // 重复调用复用同一纹理对象,避免频繁 GC
        public Texture2D BakeLut()
        {
            SortStops();

            if (_baked == null)
            {
                _baked = new Texture2D(BakeWidth, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "DicomBreakpoint_Baked",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            var pixels = new Color[BakeWidth];
            int n = _stops.Count;

            if (n == 0)
            {
                for (int i = 0; i < BakeWidth; i++) pixels[i] = Color.black;
            }
            else if (n == 1)
            {
                for (int i = 0; i < BakeWidth; i++) pixels[i] = _stops[0].Color;
            }
            else
            {
                float domMin = _stops[0].Value;
                float domMax = _stops[n - 1].Value;
                float span = Mathf.Max(domMax - domMin, 1e-5f);

                for (int i = 0; i < BakeWidth; i++)
                {
                    // 纹素中心对应的真实值
                    float t = BakeWidth > 1 ? (float)i / (BakeWidth - 1) : 0f;
                    float value = domMin + t * span;
                    pixels[i] = EvaluateColor(value);
                }
            }

            _baked.SetPixels(pixels);
            _baked.Apply(false, false);
            return _baked;
        }

        // 按真实值在断点间分段线性插值取色;值域外 Clamp 到首/尾断点色
        public Color EvaluateColor(float value)
        {
            int n = _stops.Count;
            if (n == 0) return Color.black;
            if (n == 1) return _stops[0].Color;

            if (value <= _stops[0].Value) return _stops[0].Color;
            if (value >= _stops[n - 1].Value) return _stops[n - 1].Color;

            for (int i = 0; i < n - 1; i++)
            {
                float lo = _stops[i].Value;
                float hi = _stops[i + 1].Value;
                if (value >= lo && value <= hi)
                {
                    float denom = Mathf.Max(hi - lo, 1e-5f);
                    float f = (value - lo) / denom;
                    return Color.Lerp(_stops[i].Color, _stops[i + 1].Color, f);
                }
            }
            return _stops[n - 1].Color;
        }

        // 断点按 Value 升序排序,保证烘焙与采样的值域单调
        public void SortStops()
        {
            _stops.Sort((a, b) => a.Value.CompareTo(b.Value));
        }

        // 释放烘焙纹理,Controller 销毁或更换 profile 时调用,防止纹理泄漏
        public void DestroyBaked()
        {
            if (_baked == null) return;
            if (Application.isPlaying) Destroy(_baked);
            else DestroyImmediate(_baked);
            _baked = null;
        }

        // 给一份示例断点配置(-700~700 步长 200 的 8 色),便于创建后直接可用,可在 Inspector 微调
        public void ResetToExampleStops()
        {
            _stops = new List<Stop>
            {
                new Stop { Value = -700f, Color = new Color(0f, 0f, 0.6f, 1f) },
                new Stop { Value = -500f, Color = new Color(0f, 0.5f, 1f, 1f) },
                new Stop { Value = -300f, Color = new Color(0f, 0.8f, 0.8f, 1f) },
                new Stop { Value = -100f, Color = new Color(0f, 0.8f, 0.2f, 1f) },
                new Stop { Value = 100f,  Color = new Color(0.8f, 0.9f, 0f, 1f) },
                new Stop { Value = 300f,  Color = new Color(1f, 0.6f, 0f, 1f) },
                new Stop { Value = 500f,  Color = new Color(1f, 0.3f, 0f, 1f) },
                new Stop { Value = 700f,  Color = new Color(1f, 0f, 0f, 1f) }
            };
        }
    }
}
