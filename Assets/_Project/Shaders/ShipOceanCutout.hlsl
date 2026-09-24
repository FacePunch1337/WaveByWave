#ifndef WBW_OCEAN_CUTOUT_INCLUDED
#define WBW_OCEAN_CUTOUT_INCLUDED
// These are global values, intentionally outside UnityPerMaterial.
int _WBWCutCount;
float4x4 _WBWCutMatrices[16];
float4 _WBWCutSlices[16];
TEXTURE2D_ARRAY(_WBWCutSections);
SAMPLER(sampler_WBWCutSections);
// Renderer-local section mask for the generated Stylized Water 3 surface.
float4x4 _WBWInteriorWorldToNormalized;
TEXTURE2D(_WBWInteriorCutSections);
SAMPLER(sampler_WBWInteriorCutSections);
void WBWClipOcean(float3 positionWS);

void WBWClipWater(float3 positionWS)
{
    if (_WBWInteriorWater > 0.5)
    {
        float3 p = mul(_WBWInteriorWorldToNormalized, float4(positionWS, 1)).xyz;
        clip(min(min(p.x, 1 - p.x), min(p.z, 1 - p.z)));
        // Wave displacement may cross the min/max fill height; it must not
        // punch temporary gaps in the surface along the hull.
        p.y = saturate(p.y);
        float2 sides = SAMPLE_TEXTURE2D_LOD(_WBWInteriorCutSections,
            sampler_WBWInteriorCutSections, p.zy, 0).rg;
        clip(min(p.x - sides.x, sides.y - p.x));
        return;
    }
    WBWClipOcean(positionWS);
}

void WBWClipOcean(float3 positionWS)
{
    [loop] for (int i = 0; i < _WBWCutCount; i++)
    {
        float3 p = mul(_WBWCutMatrices[i], float4(positionWS, 1)).xyz;
        [branch] if (all(p >= 0) && all(p <= 1))
        {
            float2 sides = SAMPLE_TEXTURE2D_ARRAY_LOD(_WBWCutSections, sampler_WBWCutSections,
                p.zy, _WBWCutSlices[i].x, 0).rg;
            clip((p.x >= sides.x && p.x <= sides.y) ? -1 : 1);
        }
    }
}
#endif
