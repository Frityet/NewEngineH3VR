Shader "NativeH3VR/Stock PBR"
{
    Properties
    {
        [MainTexture] _MainTex("Base Color", 2D) = "white" {}
        [MainColor] _Color("Tint", Color) = (1, 1, 1, 1)
        _MainTexVelocity("Base Texture Scroll", Vector) = (0, 0, 0, 0)
        _BaseColorVertexTint("Vertex Color Weight", Range(0, 1)) = 0
        _BumpMap("Normal Map", 2D) = "bump" {}
        _HasBumpMap("Use Normal Map", Float) = 1
        _BumpScale("Normal Strength", Float) = 1
        _DetailT("Detail Enabled", Float) = 0
        _DetailMaskSource("Detail Mask Source", Float) = 0
        [NoScaleOffset] _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailMaskStrength("Detail Mask Strength", Float) = 1
        _DetailMode("Detail Color Mode", Float) = 0
        _DetailAlbedoMap("Detail Color", 2D) = "white" {}
        _DetailAlbedoMapVelocity("Detail Scroll", Vector) = (0, 0, 0, 0)
        _DetailAlbedoMapUV("Detail UV Set", Float) = 0
        [NoScaleOffset] _DetailNormalMap("Detail Normal Map", 2D) = "bump" {}
        _DetailWeight("Detail Weight", Float) = 1
        _DetailNormalMapScale("Detail Normal Strength", Float) = 1
        _SpecTex("Metal / Occlusion / Specularity / Roughness", 2D) = "white" {}
        _Metal("Metallic", Range(0, 1)) = 1
        _Roughness("Roughness", Range(0, 1)) = 1
        _MainRoughnessSource("Roughness Source", Float) = 0
        _Occlusion("Occlusion Strength", Range(0, 1)) = 1
        _Specularity("Specularity", Range(0, 1)) = 1
        _SpecularTint("Specular Tint", Range(0, 1)) = 0
        _SpecularHighlights("Specular Highlights", Float) = 1
        _GlossyReflections("Reflection Probes", Float) = 1
        _Emission("Emission Enabled", Float) = 0
        [HDR] _EmissionColor("Emission Tint", Color) = (1, 1, 1, 1)
        _EmissionMap("Emission Mask", 2D) = "white" {}
        _IncandescenceMap("Incandescence", 2D) = "white" {}
        _IncandescenceMapVelocity("Incandescence Scroll", Vector) = (0, 0, 0, 0)
        [Gamma] _EmissionWeight("Emission Strength", Range(0, 1)) = 1
        [HideInInspector] _Mode("Stock Rendering Mode", Float) = 0
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_DetailMask); SAMPLER(sampler_DetailMask);
        TEXTURE2D(_DetailAlbedoMap); SAMPLER(sampler_DetailAlbedoMap);
        TEXTURE2D(_DetailNormalMap); SAMPLER(sampler_DetailNormalMap);
        TEXTURE2D(_SpecTex); SAMPLER(sampler_SpecTex);
        TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
        TEXTURE2D(_IncandescenceMap); SAMPLER(sampler_IncandescenceMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float4 _MainTexVelocity;
            float4 _IncandescenceMap_ST;
            float4 _IncandescenceMapVelocity;
            float4 _Color;
            float4 _EmissionColor;
            float _Mode;
            float _Cutoff;
            float _BaseColorVertexTint;
            float _HasBumpMap;
            float _BumpScale;
            float4 _DetailAlbedoMap_ST;
            float4 _DetailAlbedoMapVelocity;
            float _DetailT;
            float _DetailMaskSource;
            float _DetailMaskStrength;
            float _DetailMode;
            float _DetailAlbedoMapUV;
            float _DetailWeight;
            float _DetailNormalMapScale;
            float _Metal;
            float _Roughness;
            float _MainRoughnessSource;
            float _Occlusion;
            float _Specularity;
            float _SpecularTint;
            float _SpecularHighlights;
            float _GlossyReflections;
            float _Emission;
            float _EmissionWeight;
        CBUFFER_END

        float2 BaseUV(float2 uv)
        {
            return TRANSFORM_TEX(uv, _MainTex) + _Time.y * _MainTexVelocity.xy;
        }

        half4 StockBase(float2 uv, half4 vertexColor, out half textureAlpha)
        {
            half4 sampled = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
            textureAlpha = sampled.a;
            half4 color = sampled * _Color;
            color.rgb *= lerp(1.0h, vertexColor.rgb, _BaseColorVertexTint);
        #ifdef _ALPHATEST_ON
            clip(color.a - _Cutoff);
        #endif
            return color;
        }

        float2 StockDetailUV(float2 uv0, float2 uv1)
        {
            float2 uv = _DetailAlbedoMapUV < 0.5 ? uv0 : uv1;
            return TRANSFORM_TEX(uv, _DetailAlbedoMap) + _Time.y * _DetailAlbedoMapVelocity.xy;
        }

        half StockDetailWeight(float2 baseUV, half vertexAlpha)
        {
            half mask = vertexAlpha;
            if (_DetailMaskSource < 0.5)
                mask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, baseUV).a;
            return lerp(1.0h, mask, _DetailMaskStrength) * _DetailWeight;
        }

        half3 StockNormal(float2 uv, float2 detailUV, half detailWeight, half3 normalWS, half4 tangentWS)
        {
            if (_HasBumpMap < 0.5 || dot(tangentWS.xyz, tangentWS.xyz) < 0.01)
                return normalize(normalWS);
            half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
            if (_DetailT > 0.5)
            {
                half2 detailXY = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap,
                    detailUV), _DetailNormalMapScale * detailWeight).xy;
                // Alloy reconstructs Z for each weighted normal before blending their slopes.
                half baseZ = sqrt(saturate(1.0h - dot(normalTS.xy, normalTS.xy)));
                half detailZ = sqrt(saturate(1.0h - dot(detailXY, detailXY)));
                normalTS = half3(normalTS.xy + detailXY, baseZ * detailZ);
                normalTS = normalize(normalTS);
            }
            half3 bitangentWS = tangentWS.w * cross(normalWS, tangentWS.xyz);
            return normalize(TransformTangentToWorld(normalTS, half3x3(tangentWS.xyz, bitangentWS, normalWS)));
        }

        struct StockAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            float2 staticLightmapUV : TEXCOORD1;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct StockDepthVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            half4 tangentWS : TEXCOORD2;
            float2 detailUV : TEXCOORD3;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        StockDepthVaryings StockDepthVertex(StockAttributes input)
        {
            StockDepthVaryings output = (StockDepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = BaseUV(input.uv);
            output.detailUV = StockDetailUV(input.uv, input.staticLightmapUV);
            VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
            output.normalWS = normal.normalWS;
            output.tangentWS = half4(normal.tangentWS, input.tangentOS.w * GetOddNegativeScale());
            output.color = input.color;
            return output;
        }

        half4 StockDepthFragment(StockDepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        #ifdef _ALPHATEST_ON
            half textureAlpha;
            StockBase(input.uv, input.color, textureAlpha);
        #endif
            return input.positionCS.z;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex StockForwardVertex
            #pragma fragment StockForwardFragment
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS

            // The converter uses URP Forward with legacy baked probes. Keep runtime
            // quality switches and scene lightmaps out of the compiled variant product.
            #pragma dynamic_branch _ LIGHTMAP_ON
            #pragma dynamic_branch _ DIRLIGHTMAP_COMBINED
            #pragma dynamic_branch _ LIGHTMAP_SHADOW_MIXING
            #pragma dynamic_branch _ SHADOWS_SHADOWMASK
            #pragma dynamic_branch _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma dynamic_branch _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma dynamic_branch _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma dynamic_branch _ _LIGHT_COOKIES
            #pragma dynamic_branch _ FOG_LINEAR FOG_EXP FOG_EXP2
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DistanceFog.hlsl"

            #include "StockPbrLighting.hlsl"

            struct StockForwardVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 originalUV : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half3 normalWS : TEXCOORD3;
                half4 tangentWS : TEXCOORD4;
                float2 detailUV : TEXCOORD6;
                float4 color : COLOR;
            #if USE_LIGHTMAP_UV_INTERPOLATOR
                float2 staticLightmapUV : TEXCOORD5;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            StockForwardVaryings StockForwardVertex(StockAttributes input)
            {
                StockForwardVaryings output = (StockForwardVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = normal.normalWS;
                output.tangentWS = half4(normal.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = BaseUV(input.uv);
                output.detailUV = StockDetailUV(input.uv, input.staticLightmapUV);
                output.originalUV = input.uv;
                output.color = input.color;
            #if USE_LIGHTMAP_UV_INTERPOLATOR
                output.staticLightmapUV = TransformLightmapUV(input.staticLightmapUV, unity_LightmapST);
            #endif
                return output;
            }

            half4 StockForwardFragment(StockForwardVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half textureAlpha;
                half4 baseColor = StockBase(input.uv, input.color, textureAlpha);
                half detailWeight = 0.0h;
                if (_DetailT > 0.5)
                {
                    detailWeight = StockDetailWeight(input.uv, input.color.a);
                    // Alloy applies either straight multiplication or its linear-space MulX2 factor.
                    half multiplier = _DetailMode < 0.5 ? 1.0h : 4.594794h;
                    half3 detail = SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, input.detailUV).rgb;
                    baseColor.rgb *= lerp(1.0h, detail * multiplier, detailWeight);
                }
                baseColor.rgb = saturate(baseColor.rgb);
                half4 packed = 1.0h;
                if (_MainRoughnessSource > 0.5)
                    packed.a = textureAlpha;
                else
                    packed = SAMPLE_TEXTURE2D(_SpecTex, sampler_SpecTex, input.uv);
                half metal = saturate(packed.r * _Metal);
                half dielectric = 0.08h * _Specularity * packed.b;
                half luminance = max(dot(baseColor.rgb, half3(0.2126h, 0.7152h, 0.0722h)), 0.0001h);
                half3 dielectricTint = lerp(1.0h, baseColor.rgb / luminance, _SpecularTint);
                SurfaceData surface = (SurfaceData)0;
                surface.specular = min(0.99h, saturate(lerp(dielectric * dielectricTint, baseColor.rgb, metal)));
                // The stock diffuse term is weighted by metallic, independently of F0.
                surface.albedo = baseColor.rgb * (1.0h - metal);
                surface.smoothness = 1.0h - saturate(_Roughness * packed.a);
                half ambientMask = packed.g * (packed.g * (packed.g * 0.305306h + 0.6821711h) + 0.0125229h);
                surface.occlusion = lerp(1.0h, ambientMask, _Occlusion);
                surface.normalTS = half3(0, 0, 1);
                surface.alpha = 1.0h;
                if (_Emission > 0.5)
                {
                    float2 incandescenceUV = TRANSFORM_TEX(input.originalUV, _IncandescenceMap) + _Time.y * _IncandescenceMapVelocity.xy;
                    surface.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb *
                        SAMPLE_TEXTURE2D(_IncandescenceMap, sampler_IncandescenceMap, incandescenceUV).rgb *
                        _EmissionColor.rgb * _EmissionWeight;
                }

                InputData lighting = (InputData)0;
                lighting.positionWS = input.positionWS;
                lighting.positionCS = input.positionCS;
                lighting.normalWS = StockNormal(input.uv, input.detailUV, detailWeight, input.normalWS, input.tangentWS);
                lighting.viewDirectionWS = SafeNormalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                lighting.shadowCoord = TransformWorldToShadowCoord(input.positionWS, false);
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.preExposureMultiplier = GetPreExposureMultiplier();

                GIParams gi = (GIParams)0;
            #if USE_LIGHTMAP_UV_INTERPOLATOR
                gi.staticLightmapUV = input.staticLightmapUV;
            #endif
                gi.positionWS = input.positionWS;
                gi.normalWS = lighting.normalWS;
                gi.viewDirWS = lighting.viewDirectionWS;
                gi.positionSS = input.positionCS.xy;
                InitializeBakedGI(gi, lighting.bakedGI, lighting.shadowMask);

                URP_LIGHT_ACCUM4 color = StockEvaluateLighting(lighting, surface,
                    1.0h - surface.smoothness, _SpecularHighlights > 0.5, _GlossyReflections > 0.5);
                color.rgb = ClampExposed(lighting.preExposureMultiplier * BlendDistanceFog(color.rgb, input.positionCS));
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex StockShadowVertex
            #pragma fragment StockDepthFragment
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            StockDepthVaryings StockShadowVertex(StockAttributes input)
            {
                StockDepthVaryings output = StockDepthVertex(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 lightDirection = _LightDirection;
            #ifdef _CASTING_PUNCTUAL_LIGHT_SHADOW
                lightDirection = normalize(_LightPosition - positionWS);
            #endif
                output.positionCS = ApplyShadowClamping(
                    TransformWorldToHClip(ApplyShadowBias(positionWS, output.normalWS, lightDirection)));
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex StockDepthVertex
            #pragma fragment StockDepthFragment
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormalsOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex StockDepthVertex
            #pragma fragment StockDepthNormalsFragment
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            half4 StockDepthNormalsFragment(StockDepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #ifdef _ALPHATEST_ON
                half textureAlpha;
                StockBase(input.uv, input.color, textureAlpha);
            #endif
                half detailWeight = 0.0h;
                if (_DetailT > 0.5 && _HasBumpMap > 0.5)
                    detailWeight = StockDetailWeight(input.uv, input.color.a);
                half3 normal = StockNormal(input.uv, input.detailUV, detailWeight, input.normalWS, input.tangentWS);
            #ifdef _GBUFFER_NORMALS_OCT
                float2 oct = PackNormalOctQuadEncode(normal);
                return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0);
            #else
                return half4(normal, 0);
            #endif
            }
            ENDHLSL
        }
    }
}
