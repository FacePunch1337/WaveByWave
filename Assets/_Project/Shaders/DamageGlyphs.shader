Shader "WaveByWave/UI/Damage Glyphs"
{
    Properties { _MainTex("Font atlas",2D)="white" {} _OutlineColor("Outline",Color)=(0,0,0,1) _OutlinePixels("Outline width",Float)=1 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+50" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize, _OutlineColor; float _OutlinePixels;
            struct A { float3 position:POSITION;float2 uv:TEXCOORD0;half4 color:COLOR; };
            struct V { float4 position:SV_POSITION;float2 uv:TEXCOORD0;half4 color:COLOR; };
            V Vert(A v) { V o;o.position=TransformObjectToHClip(v.position);o.uv=v.uv;o.color=v.color;return o; }
            half4 Frag(V i):SV_Target
            {
                half a=SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv).a;
                float2 d=_MainTex_TexelSize.xy*_OutlinePixels;
                half border=max(max(SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv+float2(d.x,0)).a,
                    SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv-float2(d.x,0)).a),
                    max(SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv+float2(0,d.y)).a,
                    SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv-float2(0,d.y)).a));
                half alpha=max(a,border*_OutlineColor.a);
                return half4(lerp(_OutlineColor.rgb,i.color.rgb,a/max(alpha,.001)),alpha*i.color.a);
            }
            ENDHLSL
        }
    }
}
