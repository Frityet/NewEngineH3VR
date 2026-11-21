#ifndef NATIVEH3VR_STOCK_PBR_LIGHTING_INCLUDED
#define NATIVEH3VR_STOCK_PBR_LIGHTING_INCLUDED

float StockFresnelAttenuation(float cosine)
{
    return exp2((-5.55473 * cosine - 6.98316) * cosine);
}

float StockSpecularVisibility(float occlusion, float normalDotView)
{
    float sum = occlusion + normalDotView;
    return saturate(sum * sum + occlusion - 1.0);
}

float3 StockDirectLight(Light light, float3 normal, float3 viewDirection,
    float3 diffuse, float3 reflectivity, float roughness, float occlusion, bool specularHighlights)
{
    float normalDotLight = saturate(dot(normal, light.direction));
    float normalDotView = saturate(dot(normal, viewDirection));

    // Use the stock spherical profile for ordinary game lights. Evaluate the
    // closest direction for the half vector and conserve energy as the lobe widens.
    float3 normalOffset = dot(normal, light.direction) * normal - light.direction;
    float3 nearestLight = light.direction + normalOffset * saturate(0.1 * rsqrt(max(dot(normalOffset, normalOffset), 1e-12)));
    float inverseLightLength = rsqrt(max(dot(nearestLight, nearestLight), 1e-12));
    float3 areaLightDirection = nearestLight * inverseLightLength;
    float3 halfDirection = SafeNormalize(areaLightDirection + viewDirection);
    float lightDotHalf = saturate(dot(areaLightDirection, halfDirection));
    float lightDotHalfSquared = lightDotHalf * lightDotHalf;

    float retroReflection = roughness + 2.0 * lightDotHalfSquared - 0.5;
    float diffuseScattering = (1.0 + StockFresnelAttenuation(normalDotLight) * retroReflection) *
        (1.0 + StockFresnelAttenuation(normalDotView) * retroReflection);
    float3 response = diffuse * diffuseScattering;
    if (specularHighlights)
    {
        float alpha = 0.05 + 0.95 * roughness;
        alpha *= alpha;
        float alphaSquared = alpha * alpha;
        float normalDotHalf = saturate(dot(normal, halfDirection));
        float distributionDenominator = 1.0 + (alphaSquared - 1.0) * normalDotHalf * normalDotHalf;
        float visibilityDenominator = lerp(0.25 * alphaSquared, 1.0, lightDotHalfSquared);
        float energy = alpha / min(1.0, alpha + 0.05 * inverseLightLength);
        float strength = (alphaSquared * 0.25) * energy * energy;
        strength /= max(1e-20, visibilityDenominator * distributionDenominator * distributionDenominator);
        float3 fresnel = lerp(reflectivity, 1.0, StockFresnelAttenuation(lightDotHalf));
        response += fresnel * strength * StockSpecularVisibility(occlusion, normalDotView);
    }
    return response * light.color * (normalDotLight * light.distanceAttenuation * light.shadowAttenuation);
}

float3 StockIndirectLight(InputData data, float3 diffuse, float3 reflectivity,
    float roughness, AmbientOcclusionFactor ao, bool useReflections)
{
    float3 indirect = data.bakedGI * diffuse * ao.indirectAmbientOcclusion;
    if (useReflections)
    {
        float normalDotView = saturate(dot(data.normalWS, data.viewDirectionWS));
        float4 fit = float4(1.0, 0.0425, 1.04, -0.04) +
            roughness * float4(-1.0, -0.0275, -0.572, 0.022);
        float grazing = min(fit.x * fit.x, exp2(-9.28 * normalDotView)) * fit.x + fit.y;
        float2 coefficients = fit.zw + float2(-1.04, 1.04) * grazing;
        float o = ao.indirectAmbientOcclusion;
        float3 environment = GlossyEnvironmentReflection(reflect(-data.viewDirectionWS, data.normalWS),
            data.positionWS, roughness, 1.0, data.normalizedScreenSpaceUV, true);
        float3 fresnel = lerp(reflectivity * o, reflectivity * coefficients.x + coefficients.y,
            StockSpecularVisibility(o, normalDotView));
        indirect += fresnel * environment;
    }
    return indirect;
}

URP_LIGHT_ACCUM4 StockEvaluateLighting(InputData inputData, SurfaceData surface,
    float roughness, bool specularHighlights, bool useReflections)
{
    const bool receiveShadows = true;
    const bool transparent = false;
    float3 diffuse = surface.albedo;
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(inputData, surface, transparent);
    uint meshLayers = GetMeshRenderingLayer();
    Light main = GetMainLight(inputData, shadowMask, ao, receiveShadows, transparent);
    MixRealtimeAndBakedGI(main, inputData.normalWS, inputData.bakedGI);

    LightingData result = CreateLightingData(inputData, surface);
    result.giColor = StockIndirectLight(inputData, diffuse, surface.specular, roughness, ao, useReflections);
    if (IsMatchingLightLayer(main, meshLayers))
        result.mainLightColor = StockDirectLight(main, inputData.normalWS, inputData.viewDirectionWS, diffuse,
            surface.specular, roughness, surface.occlusion, specularHighlights);

#if defined(_ADDITIONAL_LIGHTS)
    uint count = GetAdditionalLightsCount();
#if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); ++lightIndex)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light other = GetAdditionalLight(lightIndex, inputData, shadowMask, ao, receiveShadows, transparent);
        if (IsMatchingLightLayer(other, meshLayers))
            result.additionalLightsColor += StockDirectLight(other, inputData.normalWS, inputData.viewDirectionWS, diffuse,
                surface.specular, roughness, surface.occlusion, specularHighlights);
    }
#endif
    LIGHT_LOOP_BEGIN(count)
        Light other = GetAdditionalLight(lightIndex, inputData, shadowMask, ao, receiveShadows, transparent);
        if (IsMatchingLightLayer(other, meshLayers))
            result.additionalLightsColor += StockDirectLight(other, inputData.normalWS, inputData.viewDirectionWS, diffuse,
                surface.specular, roughness, surface.occlusion, specularHighlights);
    LIGHT_LOOP_END
#endif
#if defined(_ADDITIONAL_LIGHTS_VERTEX)
    result.vertexLightingColor += inputData.vertexLighting * diffuse;
#endif
    return CalculateFinalColor(result, surface.alpha);
}

#endif
