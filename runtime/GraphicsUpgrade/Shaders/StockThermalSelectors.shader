Shader "Hidden/NativeH3VR/StockThermalSelectors"
{
    SubShader
    {
        HLSLINCLUDE
        float4 Vert(float4 position : POSITION) : SV_POSITION { return position; }
        float4 Frag() : SV_Target { return 0; }
        ENDHLSL
        Pass
        {
            Name "OPAQUE"
            Tags { "LightMode" = "NativeH3VRThermalOpaque" }
            ColorMask 0
            ZWrite Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "TRANSPARENTCUTOUT"
            Tags { "LightMode" = "NativeH3VRThermalTransparentCutout" }
            ColorMask 0
            ZWrite Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "TRANSPARENT"
            Tags { "LightMode" = "NativeH3VRThermalTransparent" }
            ColorMask 0
            ZWrite Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
