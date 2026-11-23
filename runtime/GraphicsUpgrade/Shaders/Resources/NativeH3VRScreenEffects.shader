Shader "Hidden/NativeH3VR/URP Screen Effects"
{
    Properties
    {
        _MainTex("Stock albedo", 2D) = "white" {}
        _SpecTex("Stock material", 2D) = "white" {}
        _MainRoughnessSource("Stock roughness source", Float) = 0
        _Metal("Stock metal", Float) = 0
        _Roughness("Stock roughness", Float) = 1
        _Specularity("Stock specularity", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D_X(_H3VRReflectionMask);
        TEXTURE2D_X(_H3VRReflections);
        TEXTURE2D_X(_H3VRShafts);
        float4 _H3VRScreenEffect;
        float4 _H3VRScreenFog;
        float4 _H3VRScreenSun;

        bool HasSurface(float raw)
        {
        #if UNITY_REVERSED_Z
            return raw > 0.000001;
        #else
            return raw < 0.999999;
        #endif
        }

        float3 WorldAt(float2 uv, float raw)
        {
        #if !UNITY_REVERSED_Z
            raw = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, raw);
        #endif
            return ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
        }

        float2 ProjectUv(float4 clip)
        {
            float2 ndc = clip.xy / clip.w;
        #if UNITY_UV_STARTS_AT_TOP
            ndc.y = -ndc.y;
        #endif
            return ndc * 0.5 + 0.5;
        }

        float ViewDistance(float3 world)
        {
            return -TransformWorldToView(world).z;
        }

        float ReflectionEdgeFade(float2 uv)
        {
            float2 edge = min(uv, 1.0 - uv);
            return smoothstep(0.055, 0.18, min(edge.x, edge.y));
        }

        float ReflectionSurface(float2 uv, float raw, float2 texel, out float2 adjacent)
        {
            float2 horizontal = float2(texel.x, 0.0);
            float2 vertical = float2(0.0, texel.y);
            adjacent = float2(SampleSceneDepth(uv + horizontal), SampleSceneDepth(uv + vertical));
            float2 opposite = float2(SampleSceneDepth(uv - horizontal), SampleSceneDepth(uv - vertical));
            if (!HasSurface(adjacent.x) || !HasSurface(adjacent.y) ||
                !HasSurface(opposite.x) || !HasSurface(opposite.y))
                return 0.0;
            // Device depth is affine across a planar triangle. A large second difference
            // marks a silhouette or crease, where depth cannot supply a reliable normal or hit.
            float2 error = abs(adjacent + opposite - 2.0 * raw);
        #if UNITY_REVERSED_Z
            float tolerance = max(0.0000005, raw * 0.003);
        #else
            float tolerance = max(0.0000005, (1.0 - raw) * 0.003);
        #endif
            return 1.0 - smoothstep(tolerance, tolerance * 3.0, max(error.x, error.y));
        }

        bool TraceSample(float3 world, out float2 uv, out float separation)
        {
            float4 clip = TransformWorldToHClip(world);
            uv = ProjectUv(clip);
            separation = 0.0;
            if (clip.w <= 0.0 || any(uv < 0.001) || any(uv > 0.999))
                return false;
            float raw = SampleSceneDepth(uv);
            if (!HasSurface(raw))
                return false;
            separation = ViewDistance(world) - ViewDistance(WorldAt(uv, raw));
            return true;
        }
        ENDHLSL

        Pass
        {
            Name "Stock opaque reflection receivers"
            Cull Back
            HLSLPROGRAM
            #pragma vertex ReceiverVert
            #pragma fragment ReceiverFrag
            #pragma multi_compile_instancing
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_SpecTex); SAMPLER(sampler_SpecTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _MainRoughnessSource;
                float _Metal;
                float _Roughness;
                float _Specularity;
            CBUFFER_END

            struct ReceiverInput
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct ReceiverVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 world : TEXCOORD0;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            ReceiverVaryings ReceiverVert(ReceiverInput input)
            {
                ReceiverVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.world = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.world);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }
            half2 ReceiverFrag(ReceiverVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float4 clip = TransformWorldToHClip(input.world);
                float2 uv = ProjectUv(clip);
                float raw = SampleSceneDepth(uv);
                float expected = ViewDistance(input.world);
                if (!HasSurface(raw) || abs(ViewDistance(WorldAt(uv, raw)) - expected) > 0.008 + expected * 0.0025)
                    discard;
                half4 packed = SAMPLE_TEXTURE2D(_SpecTex, sampler_SpecTex, input.uv);
                half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;
                half albedoRoughness = step(0.5, _MainRoughnessSource);
                half roughness = saturate(lerp(packed.a, alpha, albedoRoughness) * _Roughness);
                half finish = saturate((0.62 - roughness) * 3.5);
                half metal = saturate(lerp(packed.r, 1.0, albedoRoughness) * _Metal);
                half dielectric = 0.08h * saturate(_Specularity * lerp(packed.b, 1.0, albedoRoughness));
                // The depth trace resolves a sharp hit. Rough dielectrics get their broad sheen from probes.
                half polished = 1.0h - smoothstep(0.15h, 0.38h, roughness);
                half reflectance = lerp(dielectric * polished, 1.0h, metal);
                return half2(finish * reflectance, roughness);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Depth-traced reflections"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Trace
            half4 Trace(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float receiverFade = ReflectionEdgeFade(uv);
                half2 mask = SAMPLE_TEXTURE2D_X(_H3VRReflectionMask, sampler_PointClamp, uv).rg;
                if (mask.x < 0.018 || receiverFade <= 0.0)
                    return 0;
                float raw = SampleSceneDepth(uv);
                if (!HasSurface(raw))
                    return 0;
                float3 origin = WorldAt(uv, raw);
                float3 eyeToSurface = origin - GetCameraPositionWS();
                // Close held objects can see substantially different geometry in each eye.
                // Keep their material's environment reflection through that near field.
                receiverFade *= smoothstep(0.65, 1.6, length(eyeToSurface));
                float originDepth = ViewDistance(origin);
                bool dense = _H3VRScreenEffect.z > 0.5;
                if (receiverFade <= 0.0 || (dense && originDepth > 22.0))
                    return 0;

                float2 texel = rcp(_ScaledScreenParams.xy);
                float2 stride = texel * (dense ? 3.0 : 2.0);
                float2 adjacent;
                receiverFade *= ReflectionSurface(uv, raw, stride, adjacent);
                if (receiverFade <= 0.0)
                    return 0;
                float3 dx = WorldAt(uv + float2(stride.x, 0.0), adjacent.x) - origin;
                float3 dy = WorldAt(uv + float2(0.0, stride.y), adjacent.y) - origin;
                float3 incident = normalize(eyeToSurface);
                float3 normal = SafeNormalize(cross(dy, dx));
                normal = dot(normal, incident) > 0.0 ? -normal : normal;
                float3 direction = reflect(incident, normal);
                float normalView = saturate(-dot(normal, incident));
                receiverFade *= smoothstep(0.10, 0.29, normalView);
                if (receiverFade <= 0.0)
                    return 0;

                float limit = dense ? lerp(14.0, 7.0, mask.y) : lerp(17.0, 8.0, mask.y);
                int steps = dense ? 14 : 20;
                float3 start = origin + normal * 0.055;
                float previous = 0.12;
                bool inFront = false;
                [loop]
                for (int i = 0; i < steps; ++i)
                {
                    float fraction = (i + 1.0) / steps;
                    float rayDistance = 0.12 + fraction * fraction * limit;
                    float2 hitUV;
                    float separation;
                    bool valid = TraceSample(start + direction * rayDistance, hitUV, separation);
                    if (!valid)
                    {
                        if (any(hitUV < 0.001) || any(hitUV > 0.999))
                            break;
                        inFront = false;
                        continue;
                    }
                    if (inFront && separation >= 0.0)
                    {
                        float lower = previous;
                        float upper = rayDistance;
                        [unroll]
                        for (int refine = 0; refine < 4; ++refine)
                        {
                            float mid = (lower + upper) * 0.5;
                            float2 projected;
                            float delta;
                            if (TraceSample(start + direction * mid, projected, delta) && delta >= 0.0)
                                upper = mid;
                            else
                                lower = mid;
                        }
                        if (!TraceSample(start + direction * upper, hitUV, separation) ||
                            separation < 0.0 || separation > 0.09 || distance(hitUV, uv) < 0.007)
                            return 0;
                        float hitFade = ReflectionEdgeFade(hitUV) *
                            ReflectionSurface(hitUV, SampleSceneDepth(hitUV), texel * 2.0, adjacent);
                        float fade = receiverFade * hitFade * saturate((1.0 - fraction) * 3.5);
                        if (fade <= 0.0)
                            return 0;
                        float fresnel = 0.12 + 0.64 * Pow4(1.0 - normalView);
                        float weight = min(0.32, mask.x * fade * fresnel);
                        float3 incoming = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, hitUV).rgb;
                        incoming *= 1.5 / max(1.5, Max3(incoming.r, incoming.g, incoming.b));
                        half3 base = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
                        return half4((incoming - base) * weight, originDepth);
                    }
                    previous = rayDistance;
                    inFront = separation < 0.0;
                }
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Shadowed fog in-scattering"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Scatter
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float OpticalDepth(float d)
            {
                float density = _H3VRScreenFog.y;
                if (_H3VRScreenFog.x < 1.5)
                    return -log(max(0.002, 1.0 - saturate((d - _H3VRScreenFog.z) /
                        max(0.1, _H3VRScreenFog.w - _H3VRScreenFog.z))));
                if (_H3VRScreenFog.x < 2.5)
                    return density * d;
                return density * density * d * d;
            }

            half4 Scatter(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #if !defined(_MAIN_LIGHT_SHADOWS) && !defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                return 0;
            #else
                float2 uv = input.texcoord;
                float3 eye = GetCameraPositionWS();
                float raw = SampleSceneDepth(uv);
                float3 endpoint = WorldAt(uv, raw);
                bool ultra = _H3VRScreenEffect.y > 1.5;
                int steps = ultra ? 24 : 12;
                float limit = ultra ? 95.0 : 65.0;
                float3 delta = endpoint - eye;
                float lengthToSurface = min(length(delta), limit);
                float3 ray = normalize(delta);
                float phaseCos = dot(ray, normalize(_MainLightPosition.xyz));
                float phase = 0.24 / pow(max(0.18, 1.36 - 1.2 * phaseCos), 1.5);

                // The stock materials already apply fog extinction and ambient
                // fog color. Integrate only the missing light in the volume.
                float integral = 0.0;
                float previous = 1.0;
                [loop]
                for (int i = 0; i < steps; ++i)
                {
                    float t0 = i / (float)steps;
                    float t1 = (i + 1.0) / steps;
                    float d = (t0 + t1) * 0.5 * lengthToSurface;
                    float3 samplePosition = eye + ray * d;
                    float transmission = exp(-OpticalDepth(t1 * lengthToSurface));
                    float visibility = SampleMainLightRealtimeShadow(TransformWorldToShadowCoord(samplePosition), false);
                    float fade = 1.0 - GetMainLightShadowFade(samplePosition);
                    integral += max(0.0, previous - transmission) * visibility * fade;
                    previous = transmission;
                }
                return half4(min(_H3VRScreenSun.rgb, 2.0) * integral * min(0.26, 0.095 * phase), 0);
            #endif
            }
            ENDHLSL
        }

        Pass
        {
            Name "Add effects before transparent rendering"
            Blend One One
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite

            half3 ResolveReflection(float2 uv)
            {
                // Use full-resolution depth to keep a low-resolution hit on its receiver.
                // For a sloping receiver, project the local depth slope to each ray sample.
                float raw = SampleSceneDepth(uv);
                float2 slope = float2(ddx(raw), ddy(raw));
                half receiver = SAMPLE_TEXTURE2D_X(_H3VRReflectionMask, sampler_PointClamp, uv).r;
                if (receiver < 0.018 || !HasSurface(raw) || ReflectionEdgeFade(uv) <= 0.0)
                    return 0;

                uint width, height;
            #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                uint slices;
                _H3VRReflections.GetDimensions(width, height, slices);
            #else
                _H3VRReflections.GetDimensions(width, height);
            #endif
                float2 dimensions = float2(width, height);
                float2 grid = uv * dimensions - 0.5;
                int2 lower = int2(floor(grid));
                float2 blend = frac(grid);
                half3 reflection = 0;
                [unroll]
                for (int y = 0; y < 2; ++y)
                {
                    [unroll]
                    for (int x = 0; x < 2; ++x)
                    {
                        int2 pixel = clamp(lower + int2(x, y), 0, int2(width, height) - 1);
                        half4 tap = LOAD_TEXTURE2D_X(_H3VRReflections, pixel);
                        if (tap.a <= 0.0)
                            continue;
                        float2 offset = ((float2(pixel) + 0.5) / dimensions - uv) * _ScaledScreenParams.xy;
                        float expected = LinearEyeDepth(saturate(raw + dot(slope, offset)), _ZBufferParams);
                        float tolerance = 0.008 + expected * 0.003;
                        float match = 1.0 - smoothstep(tolerance, tolerance * 2.0, abs(tap.a - expected));
                        float spatial = (x == 0 ? 1.0 - blend.x : blend.x) * (y == 0 ? 1.0 - blend.y : blend.y);
                        reflection += tap.rgb * (match * spatial);
                    }
                }
                return reflection;
            }

            half4 Composite(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half3 color = 0;
                if (_H3VRScreenEffect.x > 0.0)
                    color += ResolveReflection(uv);
                if (_H3VRScreenEffect.y > 0.0)
                    color += SAMPLE_TEXTURE2D_X(_H3VRShafts, sampler_LinearClamp, uv).rgb;
                return half4(color, 0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "Character contact shadows"
            Blend DstColor Zero
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex CharacterVertex
            #pragma fragment CharacterFragment
            #pragma dynamic_branch _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float4 _H3VRCharacterRect;
            float4 _H3VRCharacterStarts[8];
            float4 _H3VRCharacterEnds[8];
            float3 _H3VRCharacterLight;
            float4 _H3VRCharacterSettings;

            struct CharacterVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            CharacterVaryings CharacterVertex(uint index : SV_VertexID)
            {
                const uint corners[6] = { 0, 1, 2, 2, 1, 3 };
                uint corner = corners[index];
                float2 uv = float2(corner & 1, corner >> 1);
                CharacterVaryings result;
                result.positionCS = float4(lerp(_H3VRCharacterRect.xy, _H3VRCharacterRect.zw, uv), 0.0, 1.0);
                return result;
            }

            float3 ClosestOnCharacterLink(float3 world, float3 first, float3 last)
            {
                float3 axis = last - first;
                return first + axis * saturate(dot(world - first, axis) / max(dot(axis, axis), 1e-6));
            }

            bool CharacterOwnsOccluder(float3 world, int count)
            {
                UNITY_LOOP
                for (int i = 0; i < count; ++i)
                {
                    float4 first = _H3VRCharacterStarts[i];
                    float3 closest = ClosestOnCharacterLink(world, first.xyz, _H3VRCharacterEnds[i].xyz);
                    if (distance(world, closest) < first.w + 0.055)
                        return true;
                }
                return false;
            }

            // Test along the receiver-to-caster segment. Depth from the body
            // itself belongs to the occluder; other opaque surfaces can block it.
            bool CharacterBlocked(float3 start, float3 end, int count, bool realtimeSun)
            {
                UNITY_UNROLL
                for (int j = 1; j <= 2; ++j)
                {
                    float2 sampleUv;
                    float separation;
                    float3 samplePosition = lerp(start, end, j * (1.0 / 3.0));
                    if (TraceSample(samplePosition, sampleUv, separation) && separation > 0.025 && separation < 0.7 &&
                        (realtimeSun || !CharacterOwnsOccluder(WorldAt(sampleUv, SampleSceneDepth(sampleUv)), count)))
                        return true;
                }
                return false;
            }

            half4 CharacterFragment(CharacterVaryings input) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(input.positionCS);
                float raw = SampleSceneDepth(uv);
                if (!HasSurface(raw))
                    discard;

                float3 world = WorldAt(uv, raw);
                float3 dx = ddx(world);
                float3 dy = ddy(world);
                float3 normal = cross(dx, dy);
                float normalLength = length(normal);
                if (normalLength < 1e-10 || max(length(dx), length(dy)) > 0.32)
                    discard;
                normal /= normalLength;
                if (dot(normal, _WorldSpaceCameraPos - world) < 0)
                    normal = -normal;

                // Only upward-facing geometry can receive this indirect contact
                // term. It follows ramps and stair treads while excluding walls
                // and undersides, with a gradual limit on very steep slopes.
                float slope = smoothstep(0.28, 0.68, normal.y);
                if (slope <= 0.0)
                    discard;

                float3 direction = _H3VRCharacterLight;
                float reach = _H3VRCharacterSettings.y;
                bool realtimeSun = _H3VRCharacterSettings.w > 0.5;
                float peak = 0.0;
                float3 blocker = world;
                int count = min((int)_H3VRCharacterSettings.x, 8);
                UNITY_LOOP
                for (int i = 0; i < count; ++i)
                {
                    float4 first = _H3VRCharacterStarts[i];
                    float3 last = _H3VRCharacterEnds[i].xyz;
                    float3 offset = world - first.xyz;
                    float3 axis = last - first.xyz;
                    float radius = first.w;
                    float3 closest = ClosestOnCharacterLink(world, first.xyz, last);
                    float3 fromBody = world - closest;
                    float selfDistance = length(fromBody);
                    if (realtimeSun ? selfDistance < radius + 0.045 :
                        selfDistance < radius + 0.015 && fromBody.y > -0.6 * radius)
                        discard;

                    if (!realtimeSun)
                    {
                        // Ambient occlusion stays immediately under the body even
                        // when the dominant local light projects the other lobe away.
                        float axisHorizontal2 = dot(axis.xz, axis.xz);
                        float underT = axisHorizontal2 > 1e-5
                            ? saturate(dot(offset.xz, axis.xz) / axisHorizontal2) : (first.y <= last.y ? 0.0 : 1.0);
                        float3 under = lerp(first.xyz, last, underT);
                        float3 underDelta = under - world;
                        if (underDelta.y >= 0.0 && underDelta.y < 1.45)
                        {
                            float height = max(0.0, underDelta.y - radius);
                            float width = radius + 0.09 + 0.28 * height;
                            float contact = (1.0 - smoothstep(0.38 * width, width, length(underDelta.xz))) *
                                (1.0 - smoothstep(0.08, 1.26, height));
                            if (contact > peak)
                            {
                                peak = contact;
                                blocker = under;
                            }
                        }
                    }

                    float3 transverseAxis = axis - direction * dot(axis, direction);
                    float transverseLength2 = dot(transverseAxis, transverseAxis);
                    float receiverDepth = -dot(offset, direction);
                    float axialLength = dot(axis, direction);
                    float t = transverseLength2 > 1e-5
                        ? saturate(dot(offset, transverseAxis) / transverseLength2)
                        : saturate((0.35 * reach - receiverDepth) / (abs(axialLength) > 1e-5 ? axialLength : 1.0));
                    float3 nearest = lerp(first.xyz, last, t);
                    float3 delta = nearest - world;
                    float depth = dot(delta, direction);
                    float verticalGap = delta.y;
                    if (depth <= 0.0 || depth >= reach + radius || verticalGap < -0.02 || verticalGap > reach)
                        continue;

                    float distanceToAxis = length(delta - direction * depth);
                    float soft = saturate((distanceToAxis - 0.42 * radius) /
                        (0.70 * radius + 0.055 + depth * (realtimeSun ? 0.29 : 0.23)));
                    float heightFade = 1.0 - smoothstep(0.14 * reach, reach, max(0.0, depth - radius));
                    float shadow = (1.0 - soft * soft * (3.0 - 2.0 * soft)) * heightFade *
                        (realtimeSun ? 1.0 : 0.78);
                    // Maximum acts as a soft union. Body links cannot stack opacity.
                    if (shadow > peak)
                    {
                        peak = shadow;
                        blocker = nearest;
                    }
                }

                if (peak < 0.005 || CharacterBlocked(world + 0.025 * normal, blocker, count, realtimeSun))
                    discard;

                float sunVisibility = 1.0;
                if (realtimeSun && (_MAIN_LIGHT_SHADOWS || _MAIN_LIGHT_SHADOWS_CASCADE))
                    sunVisibility = smoothstep(0.35, 0.85,
                        SampleMainLightRealtimeShadow(TransformWorldToShadowCoord(world), false));

                half opacity = peak * slope * _H3VRCharacterSettings.z *
                    (realtimeSun ? 0.11 * sunVisibility : 0.53);
                return half4(1.0 - opacity.xxx, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
