Shader "WaveByWave/Island Ground"
{
    Properties
    {
        _SandTex("Sand (tiling)", 2D) = "white" {}
        _RockTex("Bedrock (tiling)", 2D) = "white" {}
        _SandColor("Sand tint", Color) = (1,0.92,0.74,1)
        _RockColor("Bedrock tint", Color) = (0.65,0.65,0.65,1)
        _Tiling("Texture repeats per metre", Float) = 0.8
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
        float4 _SandColor, _RockColor;
        float _Tiling;
        CBUFFER_END
        struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float rock : TEXCOORD2; };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.rock = input.color.r;
            return output;
        }
        ENDHLSL
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_SandTex); TEXTURE2D(_RockTex);
            SAMPLER(sampler_linear_repeat);
            half4 Frag(Varyings input) : SV_Target
            {
                float3 normal = normalize(input.normalWS);
                float3 blend = pow(abs(normal), 4);
                blend /= max(0.0001, blend.x + blend.y + blend.z);
                float3 p = input.positionWS * _Tiling;
                half3 sand = (SAMPLE_TEXTURE2D(_SandTex, sampler_linear_repeat, p.yz).rgb * blend.x +
                    SAMPLE_TEXTURE2D(_SandTex, sampler_linear_repeat, p.xz).rgb * blend.y +
                    SAMPLE_TEXTURE2D(_SandTex, sampler_linear_repeat, p.xy).rgb * blend.z) * _SandColor.rgb;
                half3 rock = (SAMPLE_TEXTURE2D(_RockTex, sampler_linear_repeat, p.yz).rgb * blend.x +
                    SAMPLE_TEXTURE2D(_RockTex, sampler_linear_repeat, p.xz).rgb * blend.y +
                    SAMPLE_TEXTURE2D(_RockTex, sampler_linear_repeat, p.xy).rgb * blend.z) * _RockColor.rgb;
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 illumination = SampleSH(normal) + light.color * saturate(dot(normal, light.direction)) * light.shadowAttenuation;
                return half4(lerp(sand, rock, smoothstep(0.2, 0.75, input.rock)) * illumination, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Depth
            half4 Depth(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthNormals
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            half4 DepthNormals(Varyings input) : SV_Target
            {
                float3 normal = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 packed = PackNormalOctQuadEncode(normal) * 0.5 + 0.5;
                    return half4(PackFloat2To888(saturate(packed)), 0);
                #else
                    return half4(normal, 0);
                #endif
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment Shadow
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - output.positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                output.positionCS = TransformWorldToHClip(
                    ApplyShadowBias(output.positionWS, output.normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);
                output.rock = input.color.r;
                return output;
            }
            half4 Shadow(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
