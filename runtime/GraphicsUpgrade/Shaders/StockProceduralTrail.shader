Shader "Hidden/NativeH3VR/StockProceduralTrail"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha One
            ZClip Off
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TrailVertex
            #pragma fragment TrailFragment
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // A scalar layout matches the stock 12-byte ComputeBuffer stride on Metal.
            struct PositionWords { uint x; uint y; uint z; };
            StructuredBuffer<PositionWords> _H3VRTrailPositions;
            float4 _H3VRTrailColor;

            struct Attributes
            {
                uint index : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TrailVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                PositionWords samplePosition = _H3VRTrailPositions[input.index];
                output.positionCS = TransformWorldToHClip(asfloat(uint3(samplePosition.x, samplePosition.y, samplePosition.z)));
                return output;
            }

            half4 TrailFragment(Varyings input) : SV_Target
            {
                return _H3VRTrailColor;
            }
            ENDHLSL
        }
    }
}
