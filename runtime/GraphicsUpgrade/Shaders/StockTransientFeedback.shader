Shader "Hidden/NativeH3VR/StockTransientFeedback"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment StockFeedback
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _H3VRFeedbackProfile;
            float4 _H3VRFeedbackTint;

            half4 StockFeedback(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 offset = input.texcoord - 0.5;
                float radial = 2.0 * length(offset);
                float2 uv = input.texcoord - offset * (radial * _H3VRFeedbackProfile.x);
                half4 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
                float radius = max(_H3VRFeedbackProfile.z, 0.0001);
                float vignette = smoothstep(1.0 - radius, 1.0, radial) * _H3VRFeedbackProfile.y * _H3VRFeedbackTint.a;
                return half4(lerp(scene.rgb, _H3VRFeedbackTint.rgb, vignette), scene.a);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ViewFade"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment ViewFade
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 _H3VRFadeColor;
            half4 ViewFade(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return _H3VRFadeColor;
            }
            ENDHLSL
        }
    }
}
