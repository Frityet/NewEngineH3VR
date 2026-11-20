Shader "Hidden/EngineValidation/Raster"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Cull ("Cull", Float) = 0
        _ZWrite ("Depth write", Float) = 1
        _SrcBlend ("Source blend factor", Float) = 1
        _DstBlend ("Destination blend factor", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Cull [_Cull]
            ZWrite [_ZWrite]
            Blend [_SrcBlend] [_DstBlend]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _Color;

            Varyings vert(appdata_base vertex)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(vertex.vertex);
                output.uv = vertex.texcoord.xy;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                return tex2D(_MainTex, input.uv) * _Color;
            }
            ENDCG
        }
    }
}
