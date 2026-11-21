#ifndef NATIVEH3VR_LEGACY_DIRECTIONAL_SHADOW
#define NATIVEH3VR_LEGACY_DIRECTIONAL_SHADOW
#include "UnityCG.cginc"

#define real float
#define real2 float2
#define real3 float3
#define real4 float4
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Shadow/ShadowSamplingTent.hlsl"
#undef real4
#undef real3
#undef real2
#undef real

Texture2D<float> _MainLightShadowmapTexture;
SamplerComparisonState sampler_LinearClampCompare;
float4x4 _MainLightWorldToShadow[5];
float4 _CascadeShadowSplitSpheres0;
float4 _CascadeShadowSplitSpheres1;
float4 _CascadeShadowSplitSpheres2;
float4 _CascadeShadowSplitSpheres3;
float4 _CascadeShadowSplitSphereRadii;
float4 _MainLightShadowParams;
float4 _MainLightShadowOffset0;
float4 _MainLightShadowOffset1;
float4 _MainLightShadowmapSize;

float StockShadowCompare(float2 uv, float depth)
{
    return _MainLightShadowmapTexture.SampleCmpLevelZero(sampler_LinearClampCompare, uv, depth);
}

float StockShadowStrength(float3 positionWS, bool singleTap, float bakedOcclusion)
{
    if (!(_MAIN_LIGHT_SHADOWS || _MAIN_LIGHT_SHADOWS_CASCADE))
        return bakedOcclusion;

    float3 cameraDelta = positionWS - _WorldSpaceCameraPos;
    float fade = saturate(dot(cameraDelta, cameraDelta) * _MainLightShadowParams.z + _MainLightShadowParams.w);
    if (fade >= 1.0)
        return bakedOcclusion;

    int cascade = 0;
    if (_MAIN_LIGHT_SHADOWS_CASCADE)
    {
        float3 a = positionWS - _CascadeShadowSplitSpheres0.xyz;
        float3 b = positionWS - _CascadeShadowSplitSpheres1.xyz;
        float3 c = positionWS - _CascadeShadowSplitSpheres2.xyz;
        float3 d = positionWS - _CascadeShadowSplitSpheres3.xyz;
        float4 distances = float4(dot(a, a), dot(b, b), dot(c, c), dot(d, d));
        bool4 inside = distances < _CascadeShadowSplitSphereRadii;
        cascade = inside.x ? 0 : inside.y ? 1 : inside.z ? 2 : inside.w ? 3 : 4;
        if (cascade == 4)
            return bakedOcclusion;
    }

    float3 shadow = mul(_MainLightWorldToShadow[cascade], float4(positionWS, 1.0)).xyz;
    if (shadow.z <= 0.0 || shadow.z >= 1.0)
        return bakedOcclusion;

    float filtered = 0.0;
    float quality = singleTap ? 0.0 : _MainLightShadowParams.y;
    if (quality >= 3.0)
    {
        float weights[16];
        float2 uv[16];
        SampleShadow_ComputeSamples_Tent_7x7(_MainLightShadowmapSize, shadow.xy, weights, uv);
        UNITY_UNROLL for (int index = 0; index < 16; ++index)
            filtered += weights[index] * StockShadowCompare(uv[index], shadow.z);
    }
    else if (quality >= 2.0)
    {
        float weights[9];
        float2 uv[9];
        SampleShadow_ComputeSamples_Tent_5x5(_MainLightShadowmapSize, shadow.xy, weights, uv);
        UNITY_UNROLL for (int index = 0; index < 9; ++index)
            filtered += weights[index] * StockShadowCompare(uv[index], shadow.z);
    }
    else if (quality >= 1.0)
    {
        filtered = (StockShadowCompare(shadow.xy + _MainLightShadowOffset0.xy, shadow.z) +
            StockShadowCompare(shadow.xy + _MainLightShadowOffset0.zw, shadow.z) +
            StockShadowCompare(shadow.xy + _MainLightShadowOffset1.xy, shadow.z) +
            StockShadowCompare(shadow.xy + _MainLightShadowOffset1.zw, shadow.z)) * 0.25;
    }
    else
    {
        filtered = StockShadowCompare(shadow.xy, shadow.z);
    }

    return lerp(lerp(1.0, filtered, _MainLightShadowParams.x), bakedOcclusion, fade);
}

float StockShadowStrength(float3 positionWS, float bakedOcclusion)
{
    return StockShadowStrength(positionWS, false, bakedOcclusion);
}

float StockShadowStrength(float3 positionWS, bool singleTap)
{
    return StockShadowStrength(positionWS, singleTap, 1.0);
}

float StockShadowStrength(float3 positionWS)
{
    return StockShadowStrength(positionWS, false, 1.0);
}
#endif
