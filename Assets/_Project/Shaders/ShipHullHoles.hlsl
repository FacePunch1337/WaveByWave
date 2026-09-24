#ifndef WBW_SHIP_HOLES_INCLUDED
#define WBW_SHIP_HOLES_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
TEXTURE2D(_HoleTex); SAMPLER(sampler_HoleTex);
CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST, _BaseColor, _RegionColor, _AllowedHeight;
float _Cutoff, _ShowRegions;
int _HoleCount, _RegionCount;
float4 _Holes[24], _HolePositions[24], _HoleNormals[24], _Regions[8];
CBUFFER_END
struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 positionOS : TEXCOORD1;
    float3 normalWS : TEXCOORD2;
    float2 uv : TEXCOORD3;
    float fog : TEXCOORD4;
    float normalY : TEXCOORD5;
    UNITY_VERTEX_OUTPUT_STEREO
};
Varyings HullVert(Attributes input)
{
    Varyings o = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.positionOS = input.positionOS.xyz;
    o.positionWS = TransformObjectToWorld(o.positionOS);
    o.positionCS = TransformWorldToHClip(o.positionWS);
    o.normalWS = TransformObjectToWorldNormal(input.normalOS);
    o.normalY = abs(input.normalOS.y);
    o.uv = input.uv;
    o.fog = ComputeFogFactor(o.positionCS.z);
    return o;
}
half3 HoleTint(float2 uv, float3 positionOS)
{
    half3 tint = 1;
    bool allowed = false;
    [loop] for (int region = 0; region < min(_RegionCount, 8); region++)
        if (all(uv >= _Regions[region].xy) && all(uv <= _Regions[region].zw)) { allowed = true; break; }
    if (!allowed || positionOS.y < _AllowedHeight.x || positionOS.y > _AllowedHeight.y) return tint;
    [loop] for (int i = 0; i < min(_HoleCount, 24); i++)
    {
        float3 delta = positionOS - _HolePositions[i].xyz;
        float3 n = normalize(_HoleNormals[i].xyz);
        float3 tangent = normalize(cross(abs(n.y) < 0.9 ? float3(0,1,0) : float3(1,0,0), n));
        float3 bitangent = cross(n, tangent);
        float2 holeUV = float2(dot(delta, tangent), dot(delta, bitangent)) / max(_HolePositions[i].w * 1.25, 0.001) + 0.5;
        // Local position disambiguates overlapping atlas UV islands on opposite sides.
        if (all(holeUV >= 0) && all(holeUV <= 1) &&
            dot(delta, delta) < _HolePositions[i].w * _HolePositions[i].w)
        {
            half4 hole = SAMPLE_TEXTURE2D(_HoleTex, sampler_HoleTex, holeUV);
            clip(hole.a - _Cutoff);
            tint *= hole.rgb;
        }
    }
    return tint;
}
half4 HullFrag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(input.uv, _BaseMap)).rgb * _BaseColor.rgb;
    albedo *= HoleTint(input.uv, input.positionOS);
    if (_ShowRegions > 0.5 && input.positionOS.y >= _AllowedHeight.x && input.positionOS.y <= _AllowedHeight.y && input.normalY <= _AllowedHeight.z)
    {
        [loop] for (int i = 0; i < min(_RegionCount, 8); i++)
            if (all(input.uv >= _Regions[i].xy) && all(input.uv <= _Regions[i].zw))
            { albedo = lerp(albedo, _RegionColor.rgb, _RegionColor.a); break; }
    }
    half3 normal = normalize(input.normalWS) * IS_FRONT_VFACE(face, 1, -1);
    Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
    half3 color = albedo * (SampleSH(normal) + light.color * saturate(dot(normal, light.direction)) * light.shadowAttenuation);
    return half4(MixFog(color, input.fog), 1);
}
half4 HullDepth(Varyings input) : SV_Target
{ HoleTint(input.uv, input.positionOS); return 0; }
void HullDepthNormals(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC,
    out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    HoleTint(input.uv, input.positionOS);
    float3 normalWS = normalize(input.normalWS) * IS_FRONT_VFACE(face, 1, -1);
#if defined(_GBUFFER_NORMALS_OCT)
    float2 octNormal = PackNormalOctQuadEncode(normalWS);
    half3 packedNormal = PackFloat2To888(saturate(octNormal * .5 + .5));
    outNormalWS = half4(packedNormal, 0);
#else
    outNormalWS = half4(normalWS, 0);
#endif
#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}
#endif
