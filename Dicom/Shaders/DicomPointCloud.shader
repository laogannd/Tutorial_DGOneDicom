Shader "Dicom/PointCloud"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 0.002
    }

    // URP SubShader：当前管线为 Universal 时匹配此块
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "DicomPointsURP"
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 与 C# DicomPoint 布局一致：float3 位置 + float 强度 + float 类别
            struct DicomPoint
            {
                float3 position;
                float intensity;
                float classId;
            };

            StructuredBuffer<DicomPoint> _Points;
            float _PointSize;
            float4x4 _DicomLocalToWorld;

            // 裁剪平面(世界空间)：xyz 法线，w 为 -dot(normal, planePoint)
            float4 _DicomClipPlane;
            // 窗宽窗位：x=center, y=width(归一化空间)
            float4 _DicomWindow;
            // 外观：rgb 色调，a 强度增益
            float4 _DicomTint;
            // 显色模式：0=强度灰度，1=按类别调色板着色，2=离散 LUT 伪彩，3=断点插值显色
            float _DicomColorMode;
            // 分类调色板，索引与 DicomClassificationProfile 类别顺序一致
            float4 _DicomClassColors[16];
            // 离散查找表纹理(宽=色阶数，Point 过滤)，按归一化强度采样取伪彩色
            TEXTURE2D(_DicomLut);
            SAMPLER(sampler_DicomLut);
            // 断点插值查找表(Bilinear 过滤)，按真实值映射到断点值域采样
            TEXTURE2D(_DicomBreakpointLut);
            SAMPLER(sampler_DicomBreakpointLut);
            // 归一化范围：x=NormalizeMin, y=NormalizeMax，用于把 intensity 反推为真实值
            float4 _DicomNormalize;
            // 断点值域：x=DomainMin, y=DomainMax(首尾断点真实值)
            float4 _DicomBreakpointDomain;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float intensity : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float classId : TEXCOORD2;
            };

            // billboard quad 的 6 个顶点局部偏移(两三角)
            static const float2 kQuad[6] = {
                float2(-1,-1), float2(1,-1), float2(-1,1),
                float2(-1,1),  float2(1,-1), float2(1,1)
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                Varyings o = (Varyings)0;

                uint pointIndex = vid / 6;
                uint corner = vid % 6;
                DicomPoint p = _Points[pointIndex];

                float3 worldPos = mul(_DicomLocalToWorld, float4(p.position, 1.0)).xyz;
                o.worldPos = worldPos;

                // 面向相机的 billboard：用逆视图矩阵取相机右/上向量(两管线通用)
                float3 camRight = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 camUp = UNITY_MATRIX_I_V._m01_m11_m21;
                float2 offset = kQuad[corner] * _PointSize;
                worldPos += camRight * offset.x + camUp * offset.y;

                o.positionCS = TransformWorldToHClip(worldPos);
                o.intensity = p.intensity;
                o.classId = p.classId;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 世界空间裁剪平面：平面正侧之外的点丢弃
                if (dot(i.worldPos, _DicomClipPlane.xyz) + _DicomClipPlane.w < 0)
                    discard;

                // 窗宽窗位映射，center/width 作用于 0..1 强度
                float c = _DicomWindow.x;
                float w = max(_DicomWindow.y, 1e-4);
                float g = saturate((i.intensity - (c - w * 0.5)) / w);

                // 断点插值模式：反推真实值 -> 映射到断点值域 -> 采样平滑色带(不受窗宽窗位影响,所配即所见)
                if (_DicomColorMode > 2.5)
                {
                    float real = i.intensity * (_DicomNormalize.y - _DicomNormalize.x) + _DicomNormalize.x;
                    float dom = max(_DicomBreakpointDomain.y - _DicomBreakpointDomain.x, 1e-4);
                    float u = saturate((real - _DicomBreakpointDomain.x) / dom);
                    half4 bp = SAMPLE_TEXTURE2D(_DicomBreakpointLut, sampler_DicomBreakpointLut, float2(u, 0.5));
                    return half4(bp.rgb, 1.0);
                }

                // 离散 LUT 模式：归一化强度作 u 坐标采样伪彩查找表(Point 过滤出硬色阶)
                if (_DicomColorMode > 1.5)
                {
                    half4 lut = SAMPLE_TEXTURE2D(_DicomLut, sampler_DicomLut, float2(g, 0.5));
                    return half4(lut.rgb, 1.0);
                }

                // 分类着色模式：按类别取调色板色，乘强度做明暗；未分类(<0)落回灰度
                if (_DicomColorMode > 0.5 && i.classId >= 0)
                {
                    int idx = clamp((int)(i.classId + 0.5), 0, 15);
                    float3 classColor = _DicomClassColors[idx].rgb;
                    return half4(classColor * saturate(g + 0.2), 1.0);
                }

                // 强度增益(a)默认 1，色调(rgb)默认白；面板可实时调
                float gain = _DicomTint.a > 0 ? _DicomTint.a : 1.0;
                float3 tint = (_DicomTint.r + _DicomTint.g + _DicomTint.b) > 0 ? _DicomTint.rgb : float3(1, 1, 1);
                return half4(saturate(g * gain) * tint, 1.0);
            }
            ENDHLSL
        }
    }

    // Built-in SubShader：未匹配上面的 URP 块时(内置管线)使用此块
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Name "DicomPointsBuiltin"
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "UnityCG.cginc"

            // 与 C# DicomPoint 布局一致：float3 位置 + float 强度 + float 类别
            struct DicomPoint
            {
                float3 position;
                float intensity;
                float classId;
            };

            StructuredBuffer<DicomPoint> _Points;
            float _PointSize;
            float4x4 _DicomLocalToWorld;

            float4 _DicomClipPlane;
            float4 _DicomWindow;
            float4 _DicomTint;
            float _DicomColorMode;
            float4 _DicomClassColors[16];
            // 离散查找表纹理(宽=色阶数，Point 过滤)，按归一化强度采样取伪彩色
            sampler2D _DicomLut;
            // 断点插值查找表(Bilinear 过滤)，按真实值映射到断点值域采样
            sampler2D _DicomBreakpointLut;
            // 归一化范围：x=NormalizeMin, y=NormalizeMax，用于把 intensity 反推为真实值
            float4 _DicomNormalize;
            // 断点值域：x=DomainMin, y=DomainMax(首尾断点真实值)
            float4 _DicomBreakpointDomain;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float intensity : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float classId : TEXCOORD2;
            };

            static const float2 kQuad[6] = {
                float2(-1,-1), float2(1,-1), float2(-1,1),
                float2(-1,1),  float2(1,-1), float2(1,1)
            };

            Varyings vert(uint vid : SV_VertexID)
            {
                Varyings o = (Varyings)0;

                uint pointIndex = vid / 6;
                uint corner = vid % 6;
                DicomPoint p = _Points[pointIndex];

                float3 worldPos = mul(_DicomLocalToWorld, float4(p.position, 1.0)).xyz;
                o.worldPos = worldPos;

                // 逆视图矩阵取相机右/上向量，billboard 展开
                float3 camRight = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 camUp = UNITY_MATRIX_I_V._m01_m11_m21;
                float2 offset = kQuad[corner] * _PointSize;
                worldPos += camRight * offset.x + camUp * offset.y;

                o.positionCS = UnityWorldToClipPos(worldPos);
                o.intensity = p.intensity;
                o.classId = p.classId;
                return o;
            }

            fixed4 frag(Varyings i) : SV_Target
            {
                if (dot(i.worldPos, _DicomClipPlane.xyz) + _DicomClipPlane.w < 0)
                    discard;

                float c = _DicomWindow.x;
                float w = max(_DicomWindow.y, 1e-4);
                float g = saturate((i.intensity - (c - w * 0.5)) / w);

                // 断点插值模式：反推真实值 -> 映射到断点值域 -> 采样平滑色带(不受窗宽窗位影响,所配即所见)
                if (_DicomColorMode > 2.5)
                {
                    float real = i.intensity * (_DicomNormalize.y - _DicomNormalize.x) + _DicomNormalize.x;
                    float dom = max(_DicomBreakpointDomain.y - _DicomBreakpointDomain.x, 1e-4);
                    float u = saturate((real - _DicomBreakpointDomain.x) / dom);
                    fixed4 bp = tex2D(_DicomBreakpointLut, float2(u, 0.5));
                    return fixed4(bp.rgb, 1.0);
                }

                // 离散 LUT 模式：归一化强度作 u 坐标采样伪彩查找表(Point 过滤出硬色阶)
                if (_DicomColorMode > 1.5)
                {
                    fixed4 lut = tex2D(_DicomLut, float2(g, 0.5));
                    return fixed4(lut.rgb, 1.0);
                }

                // 分类着色模式：按类别取调色板色，乘强度做明暗；未分类(<0)落回灰度
                if (_DicomColorMode > 0.5 && i.classId >= 0)
                {
                    int idx = clamp((int)(i.classId + 0.5), 0, 15);
                    float3 classColor = _DicomClassColors[idx].rgb;
                    return fixed4(classColor * saturate(g + 0.2), 1.0);
                }

                float gain = _DicomTint.a > 0 ? _DicomTint.a : 1.0;
                float3 tint = (_DicomTint.r + _DicomTint.g + _DicomTint.b) > 0 ? _DicomTint.rgb : float3(1, 1, 1);
                return fixed4(saturate(g * gain) * tint, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
