Shader "WaveByWave/Buried Cross Decal"
{
    Properties { _Color("Cross colour", Color) = (0.8,0.015,0.01,0.9) _Width("Stroke width", Range(0.02,0.3)) = 0.1 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-50" "RenderType"="Transparent" }
        Pass
        {
            Cull Front ZWrite Off ZTest Always Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _Color; float _Width;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings Vert(Attributes input) { Varyings o; o.positionCS = TransformObjectToHClip(input.positionOS.xyz); return o; }
            half4 Frag(Varyings input) : SV_Target
            {
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float depth = SampleSceneDepth(screenUV);
                #if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
                #endif
                float3 world = ComputeWorldSpacePosition(screenUV, depth, UNITY_MATRIX_I_VP);
                float3 p = TransformWorldToObject(world);
                clip(0.5 - abs(p));
                float stroke = min(abs(p.x - p.y), abs(p.x + p.y));
                float edge = max(abs(p.x), abs(p.y));
                float alpha = (1 - smoothstep(_Width * 0.65, _Width, stroke)) * (1 - smoothstep(0.36, 0.45, edge));
                float3 n = normalize(cross(ddy(world), ddx(world)));
                alpha *= smoothstep(0.25, 0.6, abs(n.y));
                return half4(_Color.rgb, alpha * _Color.a);
            }
            ENDHLSL
        }
    }
}
