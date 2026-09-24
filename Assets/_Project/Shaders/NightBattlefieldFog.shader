Shader "Hidden/WaveByWave/Night Battlefield Fog"
{
    Properties { _FogNoiseTex("Seamless cloud noise", 2D) = "gray" {} }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Name "Infinite battlefield fog"
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_FogNoiseTex);
            SAMPLER(sampler_FogNoiseTex);
            float4 _BattlefieldCenterRadius;
            float4 _BattlefieldFogNearColor;
            float4 _BattlefieldFogFarColor;
            float4 _BattlefieldFogShape; // density, edge width, height, view distance
            float4 _BattlefieldFogNoise; // scale, wind speed, strength, sample count

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
                float rawDepth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    bool sky = rawDepth < 0.00001;
                    float farDepth = 0.00001;
                #else
                    bool sky = rawDepth > 0.99999;
                    float farDepth = 0.99999;
                #endif
                float3 eye = GetCameraPositionWS();
                float3 farPoint = ComputeWorldSpacePosition(uv, farDepth, UNITY_MATRIX_I_VP);
                float3 ray = normalize(farPoint - eye);
                float distanceLimit = 1e20;
                bool hasVisibleSurface = !sky;
                if (!sky)
                {
                    float3 surface = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                    distanceLimit = distance(eye, surface);
                }

                float2 center = _BattlefieldCenterRadius.xz;
                float radius = _BattlefieldCenterRadius.w;
                float2 eyeOffset = eye.xz - center;
                bool eyeInside = dot(eyeOffset, eyeOffset) < radius * radius;
                // Transparent ocean does not write scene depth. Stop the ray at
                // its water plane, so ocean inside the battle circle stays clear.
                float waterY = _BattlefieldCenterRadius.y;
                if (eye.y > waterY + 0.2 && ray.y < -0.001)
                {
                    float toWater = (waterY - eye.y) / ray.y;
                    if (toWater > 0.0 && toWater < distanceLimit)
                    {
                        distanceLimit = toWater;
                        hasVisibleSurface = true;
                    }
                }
                if (distanceLimit < 0.01) return scene;
                if (eyeInside && hasVisibleSurface)
                {
                    float2 visibleOffset = eye.xz + ray.xz * distanceLimit - center;
                    if (dot(visibleOffset, visibleOffset) <= radius * radius) return scene;
                }

                float startDistance = 0.0;
                if (eyeInside)
                {
                    // The circle is fixed in world space. No sample inside it
                    // contributes fog; the first sample starts at its exact edge.
                    float2 horizontalRay = ray.xz;
                    float a = dot(horizontalRay, horizontalRay);
                    if (a < 0.000001) return scene;
                    float b = dot(eyeOffset, horizontalRay);
                    float c = dot(eyeOffset, eyeOffset) - radius * radius;
                    startDistance = (-b + sqrt(max(0.0, b * b - a * c))) / a;
                    if (startDistance >= distanceLimit) return scene;
                }
                // View distance is fog depth beyond the boundary, not distance
                // from the moving camera. Even a large arena keeps its fog wall.
                distanceLimit = min(distanceLimit, startDistance + _BattlefieldFogShape.w);
                float fogLength = distanceLimit - startDistance;
                if (fogLength < 0.01) return scene;

                int samples = (int)clamp(_BattlefieldFogNoise.w, 4.0, 12.0);
                float jitter = frac(sin(dot(input.positionCS.xy, float2(12.9898, 78.233))) * 43758.5453);
                float transmittance = 1.0;
                float3 scattered = 0;
                [loop] for (int i = 0; i < samples; i++)
                {
                    // Quadratic spacing preserves detail at the circular edge
                    // without increasing the fixed sample count.
                    float t0 = i / (float)samples;
                    float t1 = (i + 1) / (float)samples;
                    float segmentStart = startDistance + fogLength * t0 * t0;
                    float segmentEnd = startDistance + fogLength * t1 * t1;
                    float segmentLength = segmentEnd - segmentStart;
                    float travel = lerp(segmentStart, segmentEnd, 0.2 + 0.6 * jitter);
                    float3 p = eye + ray * travel;
                    float radial = length(p.xz - center);
                    float wall = smoothstep(radius, radius + _BattlefieldFogShape.y, radial);
                    if (wall < 0.001) continue;
                    float vertical = p.y - _BattlefieldCenterRadius.y;
                    float heightFalloff = exp(-abs(vertical) /
                        max(1.0, _BattlefieldFogShape.z * (vertical < 0.0 ? 1.8 : 1.0)));
                    float2 noiseUV = p.xz * _BattlefieldFogNoise.x +
                        vertical * float2(0.003, -0.004) +
                        _Time.y * _BattlefieldFogNoise.y * float2(0.004, 0.002);
                    float2 noise = SAMPLE_TEXTURE2D_LOD(_FogNoiseTex, sampler_FogNoiseTex, noiseUV, 0).rg;
                    float cloud = noise.x * 0.7 + noise.y * 0.3;
                    float density = _BattlefieldFogShape.x * wall * heightFalloff *
                        lerp(1.0, 0.35 + 1.3 * cloud, _BattlefieldFogNoise.z);
                    float opacity = 1.0 - exp(-density * segmentLength);
                    float depthTint = saturate((radial - radius) /
                        max(1.0, _BattlefieldFogShape.y * 3.0));
                    float3 tint = lerp(_BattlefieldFogNearColor.rgb,
                        _BattlefieldFogFarColor.rgb, depthTint);
                    scattered += transmittance * opacity * tint;
                    transmittance *= 1.0 - opacity;
                    if (transmittance < 0.015) break;
                }
                return half4(scene.rgb * transmittance + scattered, scene.a);
            }
            ENDHLSL
        }
    }
}
