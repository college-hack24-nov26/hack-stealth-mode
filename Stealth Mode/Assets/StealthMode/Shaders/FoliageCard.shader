// Trees, bushes and grass as photographic cards: all of it one atlas, one material, a handful of draw calls.
// Vertex colour: rgb tint per plant, a = how much evening sun reaches it. Solid with depth writes, like everything else; the normal points out of the plant, so the sun side of a tree is the bright side whichever way a card faces.
Shader "StealthMode/Foliage Card"
{
    Properties
    {
        _BaseMap ("Atlas", 2D) = "white" {}
        _BaseColor ("Tint (alpha = scene opacity)", Color) = (1, 1, 1, 0.7)
        _Cutoff ("Cutoff", Range(0, 1)) = 0.4
        _FogScale ("Haze strength", Float) = 0.6
        _Sway ("Sway", Float) = 0.02
        _Ambient ("Ambient boost", Float) = 1.7
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Card"
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

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; float2 uv : TEXCOORD0; float2 sway : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 light : TEXCOORD1;      // rgb = tint x light, a = fog
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff, _FogScale, _Sway, _Ambient;
            CBUFFER_END
            half _SkyStorm;

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                float wind = sin(_Time.y * 1.3 + v.sway.y) + 0.5 * sin(_Time.y * 2.9 + v.sway.y * 1.7);
                ws.xz += wind * v.sway.x * _Sway * (1 + _SkyStorm * 3);    // only the top of a plant moves; the storm bends it harder
                o.positionCS = TransformWorldToHClip(ws);
                o.uv = v.uv;
                half3 n = normalize(TransformObjectToWorldNormal(v.normalOS));
                Light sun = GetMainLight();
                half wrap = saturate(dot(n, sun.direction) * 0.6h + 0.4h);
                half3 lit = sun.color * (wrap * v.color.a * 0.55h) + SampleSH(n) * _Ambient;
                o.light = half4(v.color.rgb * lit, ComputeFogFactor(o.positionCS.z) * _FogScale);
                return o;
            }

            // leaf edges go through MSAA: the alpha picks how many of the 4 samples a pixel covers, so they stay soft, never stepped
            half4 frag(Varyings i, out uint coverage : SV_Coverage) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half edge = (c.a - _Cutoff) / max(fwidth(c.a), 0.0001h) + 0.5h;
                clip(edge);
                coverage = edge >= 0.875h ? 15u : edge >= 0.625h ? 7u : edge >= 0.375h ? 5u : 1u;
                return half4(MixFog(c.rgb * i.light.rgb * _BaseColor.rgb, i.light.a) * _BaseColor.a, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
