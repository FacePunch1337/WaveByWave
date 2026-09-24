Shader "WaveByWave/Ships/Hull Holes"
{
    Properties
    {
        [MainTexture] _BaseMap("Hull texture", 2D) = "white" {}
        [MainColor] _BaseColor("Hull tint", Color) = (1,1,1,1)
        _HoleTex("Breach (transparent centre, opaque rim)", 2D) = "white" {}
        _Cutoff("Hole alpha cutoff", Range(0,1)) = 0.5
        [HideInInspector] _ShowRegions("Show UV regions", Float) = 0
        [HideInInspector] _RegionColor("Region tint", Color) = (0,1,0.5,0.4)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        Pass
        {
            Name "Hull"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex HullVert
            #pragma fragment HullFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "ShipHullHoles.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex HullVert
            #pragma fragment HullDepth
            #pragma multi_compile_instancing
            #include "ShipHullHoles.hlsl"
            ENDHLSL
        }
        // URP's deferred renderer builds the camera depth texture from this
        // pass for forward-only materials. Without it the underwater effect
        // sees sky depth through the hull and paints over the ship walls.
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex HullVert
            #pragma fragment HullDepthNormals
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include "ShipHullHoles.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex HullVert
            #pragma fragment HullDepth
            #pragma multi_compile_instancing
            #include "ShipHullHoles.hlsl"
            ENDHLSL
        }
    }
}
