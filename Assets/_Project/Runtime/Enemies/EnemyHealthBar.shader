Shader "WaveByWave/EnemyHealthBar"
{
    Properties
    {
        _BaseColor("Health", Color) = (0.22,0.85,0.35,1)
        _EmptyColor("Missing health", Color) = (0.2,0.035,0.03,1)
        _BorderColor("Border", Color) = (0.02,0.025,0.03,1)
        _EnemyFrame("Health fraction", Vector) = (1,0,0,0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry+10" }
        Cull Off ZWrite On ZTest LEqual
        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor, _EmptyColor, _BorderColor, _EnemyFrame;
            CBUFFER_END
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _EnemyFrame)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #define _EnemyFrame UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EnemyFrame)
            #endif
            struct Input { float3 positionOS:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Output { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
            Output Vert(Input v)
            {
                Output o = (Output)0;
                UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v,o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS); o.uv=v.uv; return o;
            }
        ENDHLSL
        Pass
        {
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            half4 Frag(Output i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 edge = min(i.uv, 1-i.uv);
                if (edge.x < 0.015 || edge.y < 0.14) return _BorderColor;
                return i.uv.x <= lerp(0.015,0.985,saturate(_EnemyFrame.x)) ? _BaseColor : _EmptyColor;
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            half4 DepthFrag(Output i):SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment NormalFrag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            void NormalFrag(Output i, out half4 normal:SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint layers:SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = TransformObjectToWorldNormal(float3(0,0,-1));
            #if defined(_GBUFFER_NORMALS_OCT)
                normal = half4(PackFloat2To888(saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5)),0);
            #else
                normal = half4(n,0);
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                layers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }
    }
}
