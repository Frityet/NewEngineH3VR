Shader "Hidden/NativeH3VR/PlayerView"
{
    Properties
    {
        [HideInInspector] _H3VRExposure ("Linear exposure multiplier", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D_X(_H3VRGlowTexture);
        float4 _H3VRLook; // bloom strength, sharpening strength, tone curve, bloom threshold
        float _H3VRExposure;

        half4 SceneColor(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
        }

        half3 Highlight(half3 color)
        {
            float peak = Max3(color.r, color.g, color.b);
            float knee = 0.4;
            float excess = clamp(peak - _H3VRLook.w + knee, 0.0, 2.0 * knee);
            excess = max(peak - _H3VRLook.w, excess * excess / (4.0 * knee));
            return min(color, 16.0) * (excess / max(peak, 0.0001));
        }

        half4 Extract(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord, d = abs(_BlitTexture_TexelSize.xy);
            half3 sum = Highlight(SceneColor(uv - d).rgb) + Highlight(SceneColor(uv + d).rgb)
                      + Highlight(SceneColor(uv + float2(d.x, -d.y)).rgb)
                      + Highlight(SceneColor(uv + float2(-d.x, d.y)).rgb);
            return half4(sum * 0.25, 1);
        }

        half4 Compose(float2 uv, bool bloom)
        {
            half4 center = SceneColor(uv);
            float3 color = max(center.rgb, 0.0);
            float2 d = abs(_BlitTexture_TexelSize.xy);
            if (_H3VRLook.y > 0.0)
            {
                float3 west = SceneColor(uv - float2(d.x, 0)).rgb;
                float3 east = SceneColor(uv + float2(d.x, 0)).rgb;
                float3 north = SceneColor(uv - float2(0, d.y)).rgb;
                float3 south = SceneColor(uv + float2(0, d.y)).rgb;
                float3 low = min(color, min(min(west, east), min(north, south)));
                float3 high = max(color, max(max(west, east), max(north, south)));
                // Limit overshoot on thin lettering, luminous lines and high-contrast edges.
                color = clamp(color + _H3VRLook.y * (color - 0.25 * (west + east + north + south)), low, high);
            }
            if (bloom)
            {
                float2 r = d * 3.5;
                half3 glow = SAMPLE_TEXTURE2D_X(_H3VRGlowTexture, sampler_LinearClamp, uv + float2(r.x,r.y)).rgb
                           + SAMPLE_TEXTURE2D_X(_H3VRGlowTexture, sampler_LinearClamp, uv + float2(-r.x,r.y)).rgb
                           + SAMPLE_TEXTURE2D_X(_H3VRGlowTexture, sampler_LinearClamp, uv + float2(r.x,-r.y)).rgb
                           + SAMPLE_TEXTURE2D_X(_H3VRGlowTexture, sampler_LinearClamp, uv - r).rgb;
                color += glow * (0.25 * _H3VRLook.x);
            }
            // Apply exposure to the composed linear color without moving the bloom threshold.
            color *= _H3VRExposure;
            if (_H3VRLook.z > 1.5) color = AcesTonemap(unity_to_ACES(color));
            else if (_H3VRLook.z > 0.5) color = NeutralTonemap(color);
            return half4(color, center.a);
        }

        half4 Finish(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return Compose(input.texcoord, false);
        }
        half4 FinishWithBloom(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return Compose(input.texcoord, true);
        }
        ENDHLSL

        Pass
        {
            Name "Extract highlights"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Extract
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            ENDHLSL
        }
        Pass
        {
            Name "Compose"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Finish
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            ENDHLSL
        }
        Pass
        {
            Name "Compose with bloom"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FinishWithBloom
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            ENDHLSL
        }
    }
}
