Shader "WaveByWave/DOTS Loot Beacon"
{
    Properties
    {
        [HDR] _BaseColor("Rarity tint", Color) = (0.8,0.25,1,1)
        _Intensity("Glow intensity", Range(0,10)) = 3
        _BeamHeight("Beam height", Range(0.5,6)) = 3.2
        _BeamWidth("Beam width", Range(0.1,1.5)) = 0.65
        _HaloSize("Base halo diameter", Range(0.2,3)) = 1.3
        _RingSize("Ground ring diameter", Range(0.2,3)) = 1.35
        _SparkSize("Round spark diameter", Range(0.02,0.25)) = 0.09
        _SparkRadius("Spark orbit radius", Range(0.05,1.2)) = 0.48
        _SparkSpeed("Spark rise speed", Range(0.05,1)) = 0.26
        _DetailDistance("Spark and ring fade distance", Range(10,150)) = 60
        [HideInInspector] _EffectSeed("Effect seed", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "Loot Beacon"
            Tags { "LightMode"="UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Exact visible item silhouettes, rendered once for the whole camera.
            // Never infer object identity from depth inside a bounding box: terrain
            // intersecting that box would acquire the same rectangular cutout.
            TEXTURE2D_X(_LootItemSilhouetteTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Intensity, _BeamHeight, _BeamWidth, _HaloSize, _RingSize;
                float _SparkSize, _SparkRadius, _SparkSpeed, _DetailDistance, _EffectSeed;
            CBUFFER_END
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float, _EffectSeed)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
                #define _EffectSeed UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _EffectSeed)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 role : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half fade : TEXCOORD1;
                half fog : TEXCOORD2;
                nointerpolation float role : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 origin = TransformObjectToWorld(float3(0,0,0));
                // Only a uniform item fade scale applies. Camera axes are normalized in
                // world space: spark cards stay circular at every camera/weapon rotation.
                float scale = length(TransformObjectToWorldDir(float3(1,0,0), false));
                float3 right = normalize(UNITY_MATRIX_I_V._m00_m10_m20);
                float3 up = normalize(UNITY_MATRIX_I_V._m01_m11_m21);
                float3 beamRight = float3(right.x, 0, right.z);
                beamRight *= rsqrt(max(dot(beamRight, beamRight), 0.0001));
                float seed = _EffectSeed;
                float time = _Time.y;
                float phase = seed * TWO_PI;
                float pulse = 0.93 + 0.07 * sin(time * 1.7 + phase);
                float height = clamp(_BeamHeight, 0.5, 6);
                float2 corner = input.uv - 0.5;
                float3 local = 0;
                float role = input.role.x;
                float detail = 1 - smoothstep(_DetailDistance * 0.65, _DetailDistance,
                    distance(GetCameraPositionWS(), origin));
                output.fade = pulse;
                if (role < 0.5)
                {
                    local = beamRight * (corner.x * clamp(_BeamWidth, 0.1, 1.5));
                    local.y += input.uv.y * height;
                }
                else if (role < 1.5)
                {
                    local = (right * corner.x + up * corner.y) * clamp(_HaloSize, 0.2, 3);
                    local.y += 0.18;
                }
                else if (role < 2.5)
                {
                    local = float3(corner.x, 0, corner.y) * clamp(_RingSize, 0.2, 3) * detail;
                    local.y = 0.045;
                    output.fade *= detail;
                }
                else
                {
                    float index = role - 3;
                    float life = frac(time * _SparkSpeed + seed + index * 0.618034);
                    float angle = phase + index * 2.399963 + time * 0.4;
                    float radius = clamp(_SparkRadius, 0.05, 1.2) * (0.65 + 0.35 * sin(index * 7 + phase));
                    local = float3(cos(angle) * radius, 0.14 + life * height * 0.78, sin(angle) * radius);
                    float diameter = clamp(_SparkSize, 0.02, 0.25) * (0.8 + 0.2 * sin(index + phase));
                    local += (right * corner.x + up * corner.y) * diameter * detail;
                    output.fade = sin(life * PI) * detail;
                }
                output.positionCS = TransformWorldToHClip(origin + local * scale);
                output.uv = input.uv;
                output.role = role;
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = UnityStereoTransformScreenSpaceTex(GetNormalizedScreenSpaceUV(input.positionCS));
                float visibleGlow = 1 - SAMPLE_TEXTURE2D_X(_LootItemSilhouetteTexture,
                    sampler_PointClamp, screenUV).r;
                clip(visibleGlow - 0.001);
                float2 p = input.uv * 2 - 1;
                float radiusSquared = dot(p,p);
                float energy;
                float whiteCore = 0;
                if (input.role < 0.5)
                {
                    float edge = saturate(1 - p.x * p.x);
                    float envelope = smoothstep(0, 0.035, input.uv.y) *
                        (1 - smoothstep(0.62, 1, input.uv.y));
                    float narrow = exp2(-p.x * p.x * 130);
                    energy = (exp2(-p.x * p.x * 6) * 0.24 + narrow * 0.58) * edge * envelope;
                    whiteCore = narrow * 0.28;
                }
                else if (input.role < 1.5)
                {
                    float soft = saturate(1 - radiusSquared);
                    float flare = exp2(-abs(p.y) * 55) * exp2(-abs(p.x) * 4);
                    float core = exp2(-radiusSquared * 48);
                    energy = soft * soft * exp2(-radiusSquared * 5) * 0.7 + core + flare * 0.22;
                    whiteCore = core * 0.7;
                }
                else if (input.role < 2.5)
                {
                    float radius = sqrt(radiusSquared);
                    float ring = 1 - smoothstep(0.025, 0.1, abs(radius - 0.66));
                    energy = ring * 0.16 + exp2(-radiusSquared * 5) * saturate(1-radiusSquared) * 0.12;
                }
                else
                {
                    // Radial mask, equal X/Y sizes. No stretched geometry or rotating cube faces.
                    float soft = saturate(1 - radiusSquared);
                    energy = soft * soft * (0.35 + exp2(-radiusSquared * 10));
                    whiteCore = exp2(-radiusSquared * 20) * 0.65;
                }
                half3 tint = lerp(_BaseColor.rgb, half3(1,1,1), whiteCore);
                half3 glow = tint * (energy * input.fade * _Intensity * _BaseColor.a * visibleGlow);
                return half4(MixFogColor(glow, half3(0,0,0), input.fog), 0);
            }
            ENDHLSL
        }
    }
}
