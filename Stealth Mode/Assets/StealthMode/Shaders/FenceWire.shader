// Chain-link wire as a solid, depth-written surface with the opacity only in alpha, like the rest of the scene. The texture's
// alpha decides coverage through MSAA, so thin wire stays soft at distance instead of breaking into bright dashes.
Shader "StealthMode/Fence Wire"
{
    Properties
    {
        _BaseMap ("Wire", 2D) = "white" {}
        _BaseColor ("Tint (alpha = scene opacity)", Color) = (1, 1, 1, 0.7)
        _Cutoff ("Cutoff", Range(0, 1)) = 0.4
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Wire"
            Tags { "LightMode" = "UniversalForward" }
            Blend One Zero
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half4 light : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                half3 n = TransformObjectToWorldNormal(v.normalOS);
                Light sun = GetMainLight();
                o.light = half4(sun.color * abs(dot(n, sun.direction)) * 0.6h + SampleSH(n), ComputeFogFactor(o.positionCS.z));
                return o;
            }

            half4 frag(Varyings i, out uint coverage : SV_Coverage) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half edge = (c.a - _Cutoff) / max(fwidth(c.a), 0.0001h) + 0.5h;
                clip(edge);
                coverage = edge >= 0.875h ? 15u : edge >= 0.625h ? 7u : edge >= 0.375h ? 5u : 1u;
                return half4(MixFog(c.rgb * _BaseColor.rgb * i.light.rgb, i.light.a) * _BaseColor.a, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
