Shader "WaveByWave/EnemyVertexAnimation"
{
    Properties
    {
        _BaseMap("Texture", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        _PositionFrames("Baked vertex positions", 2D) = "black" {}
        _NormalFrames("Baked vertex normals", 2D) = "white" {}
        _EnemyFrame("Frame A, Frame B, blend, white flash", Vector) = (0,0,0,0)
        _UseVertexColor("Combined mesh material colors", Float) = 0
        _DayMinimumLight("Day minimum light", Range(0,1)) = 0.65
        _NightMinimumLight("Night minimum light", Range(0,1)) = 0.25
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PositionFrames);
        TEXTURE2D(_NormalFrames);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _EnemyFrame;
            float _UseVertexColor;
            float _DayMinimumLight;
            float _NightMinimumLight;
        CBUFFER_END
        #ifdef UNITY_DOTS_INSTANCING_ENABLED
        UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
            UNITY_DOTS_INSTANCED_PROP(float4, _EnemyFrame)
        UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
        #define _EnemyFrame UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EnemyFrame)
        #endif
        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            float2 vertexLookup : TEXCOORD3;
            float4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float3 positionWS : TEXCOORD2;
            half fog : TEXCOORD3;
            half flash : TEXCOORD4;
            half4 tint : TEXCOORD5;
            UNITY_VERTEX_OUTPUT_STEREO
        };
        void Animate(Attributes input, out float3 position, out float3 normal)
        {
            int vertex = (int)input.vertexLookup.x;
            float4 frame = _EnemyFrame;
            position = lerp(LOAD_TEXTURE2D(_PositionFrames, int2(vertex, (int)frame.x)).xyz,
                LOAD_TEXTURE2D(_PositionFrames, int2(vertex, (int)frame.y)).xyz, frame.z);
            normal = normalize(lerp(LOAD_TEXTURE2D(_NormalFrames, int2(vertex, (int)frame.x)).xyz,
                LOAD_TEXTURE2D(_NormalFrames, int2(vertex, (int)frame.y)).xyz, frame.z));
        }
        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 position, normal;
            Animate(input, position, normal);
            output.positionWS = TransformObjectToWorld(position);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = TransformObjectToWorldNormal(normal);
            output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            output.fog = ComputeFogFactor(output.positionCS.z);
            output.flash = _EnemyFrame.w;
            output.tint = lerp(float4(1,1,1,1), input.color, _UseVertexColor);
            return output;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            half4 Frag(Varyings input) : SV_Target
            {
                half3 normal = normalize(input.normalWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb * input.tint.rgb;
                // The DOTS presentation uses no light probes, so do not rely on SH
                // coefficients that may be missing for these renderer instances.
                half mainBrightness = dot(light.color, half3(0.2126, 0.7152, 0.0722));
                half daylight = saturate((mainBrightness - 0.14h) * 2.0h);
                half minimumLight = lerp(_NightMinimumLight, _DayMinimumLight, daylight);
                half3 color = albedo * (minimumLight + light.color * saturate(dot(normal, light.direction)) *
                    light.shadowAttenuation * light.distanceAttenuation);
                color = lerp(color, half3(1,1,1), input.flash);
                return half4(MixFog(color, input.fog), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            float3 _LightDirection;
            Varyings ShadowVert(Attributes input)
            {
                Varyings output = Vert(input);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }
            half4 ShadowFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            half4 DepthFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
