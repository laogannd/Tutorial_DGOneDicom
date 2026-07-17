Shader "Dicom/PointCloud"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 0.002
    }

    // URP SubShader：当前管线为 Universal 时匹配此块
    // 双 Pass:选中点先画(写深度,不透明)确保渲染优先级高于未选中点;
    // 未选中点后画(不写深度,半透明)只作淡显背景,不遮挡已画区域
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }

        // 公共 HLSL:结构/变量/frag/kQuad,两 Pass 共享;vert 因剔除条件不同各自内联
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct DicomPoint
        {
            float3 position;
            float intensity;
            float classId;
            float selected;
        };

        StructuredBuffer<DicomPoint> _Points;
        float _PointSize;
        float4x4 _DicomLocalToWorld;
        float _DicomAlpha;
        float4 _DicomClipPlane;
        float4 _DicomWindow;
        float4 _DicomTint;
        float _DicomColorMode;
        float4 _DicomClassColors[16];
        TEXTURE2D(_DicomLut);       SAMPLER(sampler_DicomLut);
        TEXTURE2D(_DicomBreakpointLut); SAMPLER(sampler_DicomBreakpointLut);
        float4 _DicomNormalize;
        float4 _DicomBreakpointDomain;

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float intensity : TEXCOORD0;
            float3 worldPos : TEXCOORD1;
            float classId : TEXCOORD2;
            float selected : TEXCOORD3;
        };

        static const float2 kQuad[6] = {
            float2(-1,-1), float2(1,-1), float2(-1,1),
            float2(-1,1),  float2(1,-1), float2(1,1)
        };
        // 片段着色:世界裁剪平面 -> 选中/未选中 alpha -> 窗宽窗位 -> 各显色模式
        half4 fragCommon(Varyings i)
        {
            if (dot(i.worldPos, _DicomClipPlane.xyz) + _DicomClipPlane.w < 0)
                discard;

            float a = i.selected > 0.5 ? 1.0 : _DicomAlpha;

            float c = _DicomWindow.x;
            float w = max(_DicomWindow.y, 1e-4);
            float g = saturate((i.intensity - (c - w * 0.5)) / w);

            if (_DicomColorMode > 2.5)
            {
                float real = i.intensity * (_DicomNormalize.y - _DicomNormalize.x) + _DicomNormalize.x;
                float dom = max(_DicomBreakpointDomain.y - _DicomBreakpointDomain.x, 1e-4);
                float u = saturate((real - _DicomBreakpointDomain.x) / dom);
                half4 bp = SAMPLE_TEXTURE2D(_DicomBreakpointLut, sampler_DicomBreakpointLut, float2(u, 0.5));
                return half4(bp.rgb, a);
            }
            if (_DicomColorMode > 1.5)
            {
                half4 lut = SAMPLE_TEXTURE2D(_DicomLut, sampler_DicomLut, float2(g, 0.5));
                return half4(lut.rgb, a);
            }
            if (_DicomColorMode > 0.5 && i.classId >= 0)
            {
                int idx = clamp((int)(i.classId + 0.5), 0, 15);
                float3 classColor = _DicomClassColors[idx].rgb;
                return half4(classColor * saturate(g + 0.2), a);
            }
            float gain = _DicomTint.a > 0 ? _DicomTint.a : 1.0;
            float3 tint = (_DicomTint.r + _DicomTint.g + _DicomTint.b) > 0 ? _DicomTint.rgb : float3(1, 1, 1);
            return half4(saturate(g * gain) * tint, a);
        }

        // 顶点着色:KeepSelected 决定本 Pass 只渲染选中(1)或未选中(0)点;
        // 不属于本 Pass 的点退化为零面积三角形(顶点重合)被光栅化丢弃
        Varyings vertCommon(uint vid, float keepSelected)
        {
            Varyings o = (Varyings)0;
            uint pointIndex = vid / 6;
            uint corner = vid % 6;
            DicomPoint p = _Points[pointIndex];

            bool isSel = p.selected > 0.5;
            if ((keepSelected > 0.5) != isSel)
            {
                o.positionCS = float4(-2, -2, -2, 1); // NDC 外,整三角被裁剪
                return o;
            }

            float3 worldPos = mul(_DicomLocalToWorld, float4(p.position, 1.0)).xyz;
            o.worldPos = worldPos;
            float3 camRight = UNITY_MATRIX_I_V._m00_m10_m20;
            float3 camUp = UNITY_MATRIX_I_V._m01_m11_m21;
            float2 offset = kQuad[corner] * _PointSize;
            worldPos += camRight * offset.x + camUp * offset.y;

            o.positionCS = TransformWorldToHClip(worldPos);
            o.intensity = p.intensity;
            o.classId = p.classId;
            o.selected = p.selected;
            return o;
        }
        ENDHLSL
        // Pass 1:选中点 -> 不透明,写深度,先渲染占住深度,后续未选中点无法遮挡
        Pass
        {
            Name "DicomSelectedURP"
            // URP 前向绘制对同一 LightMode 只画第一个匹配 Pass;两 Pass 须标不同 LightMode 才都渲染
            // 选中 Pass 标 SRPDefaultUnlit(tag 列表在前,先画占深度)
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            Varyings vert(uint vid : SV_VertexID) { return vertCommon(vid, 1.0); }
            half4 frag(Varyings i) : SV_Target { return fragCommon(i); }
            ENDHLSL
        }

        // Pass 2:未选中点 -> 半透明淡显,不写深度,只作背景不遮挡已画区域
        Pass
        {
            Name "DicomFadedURP"
            // 未选中淡显 Pass 标 UniversalForward(与选中 Pass 不同 LightMode),URP 才会两个都画
            // 否则同 LightMode 下此 Pass 被静默丢弃,全部 Selected=0 的点(幽灵底图/未选淡显)不显示
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            Varyings vert(uint vid : SV_VertexID) { return vertCommon(vid, 0.0); }
            half4 frag(Varyings i) : SV_Target { return fragCommon(i); }
            ENDHLSL
        }
    }
    // Built-in SubShader：未匹配 URP 时(内置管线)使用此块,同样双 Pass
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        // 用 CGINCLUDE/CGPROGRAM 而非 HLSLINCLUDE:CGINCLUDE 只注入 CGPROGRAM 块,
        // 与上面 URP 的 HLSLINCLUDE(只注入 HLSLPROGRAM 块)互不污染,避免 _Time 等重定义
        CGINCLUDE
        #pragma target 4.5
        #include "UnityCG.cginc"

        struct DicomPoint
        {
            float3 position;
            float intensity;
            float classId;
            float selected;
        };

        StructuredBuffer<DicomPoint> _Points;
        float _PointSize;
        float4x4 _DicomLocalToWorld;
        float _DicomAlpha;
        float4 _DicomClipPlane;
        float4 _DicomWindow;
        float4 _DicomTint;
        float _DicomColorMode;
        float4 _DicomClassColors[16];
        sampler2D _DicomLut;
        sampler2D _DicomBreakpointLut;
        float4 _DicomNormalize;
        float4 _DicomBreakpointDomain;

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float intensity : TEXCOORD0;
            float3 worldPos : TEXCOORD1;
            float classId : TEXCOORD2;
            float selected : TEXCOORD3;
        };

        static const float2 kQuad[6] = {
            float2(-1,-1), float2(1,-1), float2(-1,1),
            float2(-1,1),  float2(1,-1), float2(1,1)
        };

        half4 fragCommon(Varyings i)
        {
            if (dot(i.worldPos, _DicomClipPlane.xyz) + _DicomClipPlane.w < 0)
                discard;

            float a = i.selected > 0.5 ? 1.0 : _DicomAlpha;
            float c = _DicomWindow.x;
            float w = max(_DicomWindow.y, 1e-4);
            float g = saturate((i.intensity - (c - w * 0.5)) / w);

            if (_DicomColorMode > 2.5)
            {
                float real = i.intensity * (_DicomNormalize.y - _DicomNormalize.x) + _DicomNormalize.x;
                float dom = max(_DicomBreakpointDomain.y - _DicomBreakpointDomain.x, 1e-4);
                float u = saturate((real - _DicomBreakpointDomain.x) / dom);
                half4 bp = tex2D(_DicomBreakpointLut, float2(u, 0.5));
                return half4(bp.rgb, a);
            }
            if (_DicomColorMode > 1.5)
            {
                half4 lut = tex2D(_DicomLut, float2(g, 0.5));
                return half4(lut.rgb, a);
            }
            if (_DicomColorMode > 0.5 && i.classId >= 0)
            {
                int idx = clamp((int)(i.classId + 0.5), 0, 15);
                float3 classColor = _DicomClassColors[idx].rgb;
                return half4(classColor * saturate(g + 0.2), a);
            }
            float gain = _DicomTint.a > 0 ? _DicomTint.a : 1.0;
            float3 tint = (_DicomTint.r + _DicomTint.g + _DicomTint.b) > 0 ? _DicomTint.rgb : float3(1, 1, 1);
            return half4(saturate(g * gain) * tint, a);
        }

        Varyings vertCommon(uint vid, float keepSelected)
        {
            Varyings o = (Varyings)0;
            uint pointIndex = vid / 6;
            uint corner = vid % 6;
            DicomPoint p = _Points[pointIndex];

            bool isSel = p.selected > 0.5;
            if ((keepSelected > 0.5) != isSel)
            {
                o.positionCS = float4(-2, -2, -2, 1);
                return o;
            }

            float3 worldPos = mul(_DicomLocalToWorld, float4(p.position, 1.0)).xyz;
            o.worldPos = worldPos;
            float3 camRight = UNITY_MATRIX_I_V._m00_m10_m20;
            float3 camUp = UNITY_MATRIX_I_V._m01_m11_m21;
            float2 offset = kQuad[corner] * _PointSize;
            worldPos += camRight * offset.x + camUp * offset.y;

            o.positionCS = UnityWorldToClipPos(worldPos);
            o.intensity = p.intensity;
            o.classId = p.classId;
            o.selected = p.selected;
            return o;
        }
        ENDCG

        Pass
        {
            Name "DicomSelectedBuiltin"
            Cull Off
            ZWrite On
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            Varyings vert(uint vid : SV_VertexID) { return vertCommon(vid, 1.0); }
            half4 frag(Varyings i) : SV_Target { return fragCommon(i); }
            ENDCG
        }

        Pass
        {
            Name "DicomFadedBuiltin"
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            Varyings vert(uint vid : SV_VertexID) { return vertCommon(vid, 0.0); }
            half4 frag(Varyings i) : SV_Target { return fragCommon(i); }
            ENDCG
        }
    }

    Fallback Off
}
