// The sky as real geometry. The headset clears to transparent for passthrough, so a skybox never draws there; this dome
// does, and takes the same opacity as the rest of the scene. Drawn after the land (see OpacitySlider), so only the pixels
// no land covers pay for it and the land never shows sky through it.
Shader "StealthMode/Sky Dome"
{
    Properties
    {
        _BaseMap ("Sky", 2D) = "white" {}
        _BaseColor ("Tint (alpha = scene opacity)", Color) = (1, 1, 1, 0.7)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Sky"
            Tags { "LightMode" = "UniversalForward" }
            Blend One Zero
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END
            half _SkyStorm, _SkyFlash;     // set by Weather: 0 on a clear evening

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                c = lerp(c, unity_FogColor.rgb * 0.8h, _SkyStorm * 0.9h) + _SkyFlash * 0.6h;
                return half4(c * _BaseColor.a, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
