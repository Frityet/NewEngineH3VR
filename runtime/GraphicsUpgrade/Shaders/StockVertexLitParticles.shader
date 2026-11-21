Shader "NativeH3VR/Stock Vertex-Lit Particles"
{
    Properties
    {
        _EmisColor ("Emissive Color", Color) = (0.2, 0.2, 0.2, 0)
        _MainTex ("Particle Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB
            ZWrite Off
            Cull Off
            ZClip Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _EmisColor;
            float4 _NativeH3VRVertexLightColor[8];
            float4 _NativeH3VRVertexLightPosition[8];
            float4 _NativeH3VRVertexLightAtten[8];
            float4 _NativeH3VRVertexSpotDirection[8];

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fog : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(appdata_full input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                float3 worldNormal = UnityObjectToWorldNormal(input.normal);
                float3 rgb = input.color.rgb * glstate_lightmodel_ambient.rgb + _EmisColor.rgb;
                [unroll] for (int index = 0; index < 8; ++index)
                {
                    float4 lightPosition = _NativeH3VRVertexLightPosition[index];
                    float4 attenuation = _NativeH3VRVertexLightAtten[index];
                    float3 lightVector = lightPosition.xyz - worldPosition * lightPosition.w;
                    float distanceSquared = dot(lightVector, lightVector);
                    float3 lightDirection = lightVector * rsqrt(max(distanceSquared, 0.000001));
                    float falloff = rcp(1.0 + attenuation.z * distanceSquared);
                    if (lightPosition.w != 0 && distanceSquared * attenuation.z > 25.0)
                        falloff = 0.0;
                    if (attenuation.x > -1.0 && lightPosition.w != 0)
                        falloff *= saturate((max(dot(lightDirection, _NativeH3VRVertexSpotDirection[index].xyz), 0.0)
                            - attenuation.x) * attenuation.y);
                    float diffuse = max(dot(worldNormal, lightDirection), 0.0);
                    rgb += min(input.color.rgb * _NativeH3VRVertexLightColor[index].rgb * (0.5 * diffuse * falloff), 1.0);
                }

                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
                output.color = saturate(float4(rgb, input.color.a));
                output.fog = 1.0;
                #if defined(FOG_EXP) || defined(FOG_EXP2)
                    float viewDistance = length(mul(UNITY_MATRIX_V, float4(worldPosition, 1)).xyz);
                    #if defined(FOG_EXP)
                        output.fog = exp2(-viewDistance * unity_FogParams.y);
                    #else
                        float density = viewDistance * unity_FogParams.x;
                        output.fog = exp2(-density * density);
                    #endif
                #endif
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv) * input.color;
                color.rgb = lerp(unity_FogColor.rgb, color.rgb, input.fog);
                return color;
            }
            ENDCG
        }

        UsePass "Hidden/NativeH3VR/StockThermalSelectors/OPAQUE"
        UsePass "Hidden/NativeH3VR/StockThermalSelectors/TRANSPARENT"
        UsePass "Hidden/NativeH3VR/StockThermalSelectors/TRANSPARENTCUTOUT"
    }
}
