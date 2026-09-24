Shader "WaveByWave/Ships/Interior Water"
{
    Properties
    {
        _DeepColor("Deep water", Color) = (0.015,0.19,0.23,0.88)
        _CrestColor("Crest", Color) = (0.22,0.65,0.68,0.8)
        _Ripples("Height / frequency / speed / smoothness", Vector) = (0.025,3,1.4,0.85)
        _FillHeight("Local water level", Float) = 0
        [NoScaleOffset] _CutSections("Hull sections", 2D) = "white" {}
        _HasCutSections("Clip to hull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent-2" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_CutSections);
            SAMPLER(sampler_CutSections);
            CBUFFER_START(UnityPerMaterial)
            float4 _DeepColor, _CrestColor, _Ripples;
            float _FillHeight, _HasCutSections;
            float4x4 _CutWorldToNormalized;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float2 uv : TEXCOORD1; float fog : TEXCOORD2; float localY : TEXCOORD3; UNITY_VERTEX_OUTPUT_STEREO };
            V vert(A i)
            {
                V o = (V)0; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float phase = _Time.y * _Ripples.z;
                float wave = sin(i.positionOS.x * _Ripples.y + phase) * cos(i.positionOS.z * _Ripples.y * 0.8 - phase) * _Ripples.x;
                i.positionOS.y = _FillHeight + wave;
                o.positionWS = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = i.positionOS.xz;
                o.localY = i.positionOS.y;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float phase = _Time.y * _Ripples.z;
                float2 uv = i.uv * _Ripples.y;
                if (_HasCutSections > 0.5f)
                {
                    float3 cut = mul(_CutWorldToNormalized, float4(i.positionWS, 1)).xyz;
                    clip(min(min(cut.x, 1 - cut.x), min(min(cut.y, 1 - cut.y), min(cut.z, 1 - cut.z))));
                    float2 sides = SAMPLE_TEXTURE2D(_CutSections, sampler_CutSections, cut.zy).rg;
                    clip(min(cut.x - sides.x, sides.y - cut.x));
                }
                float dx = cos(uv.x + phase) * cos(uv.y * 0.8 - phase) * _Ripples.x * _Ripples.y;
                float dz = -sin(uv.x + phase) * sin(uv.y * 0.8 - phase) * _Ripples.x * _Ripples.y * 0.8;
                half3 normal = TransformObjectToWorldNormal(normalize(float3(-dx, 1, -dz)));
                half3 view = SafeNormalize(GetWorldSpaceViewDir(i.positionWS));
                Light light = GetMainLight();
                half fresnel = pow(1 - saturate(abs(dot(normal, view))), 4);
                half4 color = lerp(_DeepColor, _CrestColor, fresnel * 0.7 + 0.12 * sin(uv.x + phase));
                half specular = pow(saturate(dot(normal, normalize(light.direction + view))), lerp(12, 180, _Ripples.w));
                color.rgb *= SampleSH(normal) + light.color * (0.35 + 0.65 * saturate(dot(normal, light.direction)));
                color.rgb += light.color * specular * 0.5;
                color.rgb = MixFog(color.rgb, i.fog);
                return color;
            }
            ENDHLSL
        }
    }
}
