Shader "WaveByWave/Ships/Water Cut"
{
    SubShader
    {
        // Invisible authoring material. ShipOceanCutout provides the volume to SW3.
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-1" }
        Pass
        {
            Name "WaterCut"
            // URP does not draw this pass. The renderer is only a shape/enable flag;
            // the ocean itself performs clipping, with no extra hull draw call.
            Tags { "LightMode"="ShipOceanCutout" }
            // The ocean shader clips against ShipOceanCutout's mesh sections.
            // Never write camera depth: that hides unrelated transparent effects
            // and cannot remove water above the bottom of an open hull.
            ColorMask 0 ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            V vert(A i)
            { V o = (V)0; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.positionCS = TransformObjectToHClip(i.positionOS.xyz); return o; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
    CustomEditor "WaveByWave.Editor.ShipOceanCutoutMaterialEditor"
}
