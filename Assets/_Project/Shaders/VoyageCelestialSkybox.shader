Shader "WaveByWave/Skybox/Cubemap Blend Celestial"
{
    Properties
    {
        [NoScaleOffset] _Tex ("Day Cubemap (HDR)", Cube) = "black" {}
        [NoScaleOffset] _Tex_Blend ("Night Cubemap (HDR)", Cube) = "black" {}
        _CubemapTransition ("Night Blend", Range(0, 1)) = 0
        _Exposure ("Exposure", Range(0, 8)) = 1
        [Gamma] _TintColor ("Tint", Color) = (0.5, 0.5, 0.5, 1)
        _CubemapPosition ("Cubemap Position", Float) = 0
        [Toggle] _EnableRotation ("Enable Rotation", Float) = 0
        _Rotation ("Rotation", Range(0, 360)) = 0
        _RotationSpeed ("Rotation Speed", Float) = 1
        [Toggle] _EnableFog ("Enable Fog", Float) = 0
        _FogIntensity ("Fog Intensity", Range(0, 1)) = 1
        _FogHeight ("Fog Height", Range(0, 1)) = 1
        _FogSmoothness ("Fog Smoothness", Range(0.01, 1)) = 0.01
        _FogFill ("Fog Fill", Range(0, 1)) = 0.5
        _FogPosition ("Fog Position", Float) = 0
        [HideInInspector] _Tex_HDR ("Day HDR Decode", Vector) = (1, 1, 0, 0)
        [HideInInspector] _Tex_Blend_HDR ("Night HDR Decode", Vector) = (1, 1, 0, 0)

        [Header(Sun)]
        _SunDirection ("Sun Direction (world XYZ)", Vector) = (0, 0.7, 0.7, 0)
        _SunVisibility ("Sun Visibility", Range(0, 1)) = 1
        [NoScaleOffset] _SunSprite ("Sun Sprite Texture (RGBA)", 2D) = "white" {}
        [Toggle] _UseSunSprite ("Use Sun Sprite", Float) = 0
        _SunSpriteRect ("Sun Sprite UV Rect", Vector) = (0, 0, 1, 1)
        _SunAngularRadius ("Sun Angular Radius", Range(0.1, 10)) = 1.5
        _SunEdgeSoftness ("Sun Edge Softness", Range(0.01, 1)) = 0.08
        _SunHaloRadius ("Sun Halo Radius", Range(0, 20)) = 4
        _SunGlow ("Sun Halo Strength", Range(0, 2)) = 0.18
        [HDR] _SunColor ("Sun Color / Brightness", Color) = (2, 1.8, 1.3, 1)

        [Header(Moon)]
        _MoonDirection ("Moon Direction (world XYZ)", Vector) = (0, 0.7, -0.7, 0)
        _MoonVisibility ("Moon Visibility", Range(0, 1)) = 0
        [NoScaleOffset] _MoonSprite ("Moon Sprite Texture (RGBA)", 2D) = "white" {}
        [Toggle] _UseMoonSprite ("Use Moon Sprite", Float) = 0
        _MoonSpriteRect ("Moon Sprite UV Rect", Vector) = (0, 0, 1, 1)
        _MoonAngularRadius ("Moon Angular Radius", Range(0.1, 10)) = 1.1
        _MoonEdgeSoftness ("Moon Edge Softness", Range(0.01, 1)) = 0.06
        _MoonHaloRadius ("Moon Halo Radius", Range(0, 20)) = 2
        _MoonGlow ("Moon Halo Strength", Range(0, 2)) = 0.12
        [HDR] _MoonColor ("Moon Color / Brightness", Color) = (0.98, 1.1, 1.25, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Background" "Queue"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "UnityShaderVariables.cginc"

            samplerCUBE _Tex;
            samplerCUBE _Tex_Blend;
            half4 _Tex_HDR;
            half4 _Tex_Blend_HDR;
            half _CubemapTransition;
            half4 _TintColor;
            half _Exposure;
            float _CubemapPosition;
            half _Rotation;
            half _RotationSpeed;
            half _EnableRotation;
            half _EnableFog;
            float _FogPosition;
            half _FogHeight;
            half _FogSmoothness;
            half _FogFill;
            half _FogIntensity;

            float4 _SunDirection;
            float4 _MoonDirection;
            sampler2D _SunSprite;
            sampler2D _MoonSprite;
            float4 _SunSpriteRect;
            float4 _MoonSpriteRect;
            half _UseSunSprite;
            half _UseMoonSprite;
            float _SunAngularRadius, _MoonAngularRadius;
            float _SunEdgeSoftness, _MoonEdgeSoftness;
            float _SunHaloRadius, _MoonHaloRadius;
            half4 _SunColor;
            half4 _MoonColor;
            half _SunVisibility;
            half _MoonVisibility;
            half _SunGlow;
            half _MoonGlow;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 cubeDirection : TEXCOORD0;
                float3 celestialDirection : TEXCOORD1;
                float fogHeight : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float cameraMode = lerp(1.0, unity_OrthoParams.y / max(0.001, unity_OrthoParams.x),
                    unity_OrthoParams.w);
                float3 direction = float3(v.vertex.x, v.vertex.y * cameraMode, v.vertex.z);
                float3 cubeDirection = direction;
                if (_EnableRotation > 0.5)
                {
                    float angle = 1.0 - radians(_Rotation + _Time.y * _RotationSpeed);
                    float cosine = cos(angle);
                    float sine = sin(angle);
                    cubeDirection.xz = float2(direction.x * cosine + direction.z * sine,
                        direction.z * cosine - direction.x * sine);
                }
                cubeDirection.y -= _CubemapPosition;
                o.cubeDirection = cubeDirection;
                o.celestialDirection = normalize(direction);
                o.fogHeight = v.vertex.y;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float3 AddCelestial(float3 sky, float3 direction, float3 bodyDirection,
                float radius, float softness, float haloRadius, sampler2D sprite,
                float4 spriteRect, half useSprite, half3 color, half visibility, half glowStrength)
            {
                if (visibility <= 0.001) return sky;
                float3 center = normalize(bodyDirection);
                float alignment = dot(direction, center);
                float outerDisc = cos(radians(radius + softness));
                float innerDisc = cos(radians(max(0.0, radius - softness)));
                float disc = smoothstep(outerDisc, innerDisc, alignment);
                float haloEdge = cos(radians(radius + max(haloRadius, softness + 0.01)));
                float halo = saturate((alignment - haloEdge) / max(0.00001, outerDisc - haloEdge)) * (1.0 - disc);
                if (useSprite > 0.5 && alignment > 0.0)
                {
                    float3 reference = abs(center.y) > 0.95 ? float3(0, 0, 1) : float3(0, 1, 0);
                    float3 right = normalize(cross(reference, center));
                    float3 up = cross(center, right);
                    float scale = max(0.0001, 2.0 * sin(radians(radius)));
                    float2 uv = float2(0.5, 0.5) + float2(dot(direction, right), dot(direction, up)) / scale;
                    if (all(uv >= 0.0) && all(uv <= 1.0))
                    {
                        half4 sampled = tex2D(sprite, spriteRect.xy + uv * spriteRect.zw);
                        sky = lerp(sky, sampled.rgb * color, saturate(sampled.a * visibility));
                    }
                    return sky + color * visibility * halo * glowStrength;
                }
                return sky + color * visibility * (disc + halo * glowStrength);
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 day = DecodeHDR(texCUBE(_Tex, i.cubeDirection), _Tex_HDR);
                half3 night = DecodeHDR(texCUBE(_Tex_Blend, i.cubeDirection), _Tex_Blend_HDR);
                float3 sky = lerp(day, night, saturate(_CubemapTransition)) *
                    unity_ColorSpaceDouble.rgb * _TintColor.rgb * _Exposure;
                if (_EnableFog > 0.5)
                {
                    float fogFactor = saturate(pow(saturate(abs(i.fogHeight - _FogPosition) /
                        max(0.0001, _FogHeight)), 1.0 - _FogSmoothness));
                    float skyWeight = lerp(1.0, fogFactor * (1.0 - _FogFill), _FogIntensity);
                    sky = lerp(unity_FogColor.rgb, sky, skyWeight);
                }
                float3 direction = normalize(i.celestialDirection);
                sky = AddCelestial(sky, direction, _SunDirection.xyz, _SunAngularRadius,
                    _SunEdgeSoftness, _SunHaloRadius, _SunSprite, _SunSpriteRect, _UseSunSprite,
                    _SunColor.rgb, _SunVisibility, _SunGlow);
                sky = AddCelestial(sky, direction, _MoonDirection.xyz, _MoonAngularRadius,
                    _MoonEdgeSoftness, _MoonHaloRadius, _MoonSprite, _MoonSpriteRect, _UseMoonSprite,
                    _MoonColor.rgb, _MoonVisibility, _MoonGlow);
                return half4(sky, 1.0);
            }
            ENDCG
        }
    }
    Fallback "Skybox/Cubemap"
    CustomEditor "WaveByWave.Editor.VoyageCelestialSkyboxGUI"
}
