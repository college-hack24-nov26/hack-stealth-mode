// The land between the fence and the painted backdrop. Photo textures blended per pixel by big soft patches, never by mesh
// vertices, so no block edges show. Sun per pixel with a detail normal that catches the low evening light; depth-written and
// solid like everything else in the scene, with the opacity carried only in alpha.
Shader "StealthMode/Hills"
{
    Properties
    {
        _BaseColor ("Tint (alpha = scene opacity)", Color) = (1, 1, 1, 0.7)
        _GrassMap ("Grass", 2D) = "white" {}
        _PatchMap ("Big patches", 2D) = "gray" {}
        _DryMap ("Ground at the compound edge", 2D) = "gray" {}
        _DetailNormal ("Detail normal", 2D) = "bump" {}
        _GrassTile ("Grass metres per tile", Float) = 8.5
        _DryTile ("Dry metres per tile", Float) = 4
        _FogScale ("Haze strength", Float) = 0.6
        _GrassGain ("Grass gain", Float) = 2.2
        _Ambient ("Ambient boost", Float) = 1.5
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Hills"
            Tags { "LightMode" = "UniversalForward" }
            Blend One Zero
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 xz : TEXCOORD0;
                half4 mix : TEXCOORD1;      // g dry ground, b shade, a sun reach
                half4 ambient : TEXCOORD2;  // rgb ambient, a fog
                half3 normal : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_GrassMap); SAMPLER(sampler_GrassMap);
            TEXTURE2D(_PatchMap); SAMPLER(sampler_PatchMap);
            TEXTURE2D(_DryMap); SAMPLER(sampler_DryMap);
            TEXTURE2D(_DetailNormal); SAMPLER(sampler_DetailNormal);
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _GrassTile, _DryTile;
                half _FogScale, _GrassGain, _Ambient;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.xz = ws.xz;
                o.normal = TransformObjectToWorldNormal(v.normalOS);
                o.mix = v.color;
                o.ambient = half4(SampleSH(o.normal) * _Ambient, ComputeFogFactor(o.positionCS.z) * _FogScale);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 near = SAMPLE_TEXTURE2D(_GrassMap, sampler_GrassMap, i.xz / _GrassTile).rgb;
                half3 mid = SAMPLE_TEXTURE2D(_GrassMap, sampler_GrassMap, i.xz / (_GrassTile * 6.7) + 0.37).rgb;
                half patch = dot(SAMPLE_TEXTURE2D(_PatchMap, sampler_PatchMap, i.xz / 97).rgb, half3(0.4, 0.5, 0.1));
                half3 grass = lerp(near, mid, 0.45h) * _GrassGain * lerp(0.72h, 1.3h, saturate(patch * 3.5h));
                half3 dry = SAMPLE_TEXTURE2D(_DryMap, sampler_DryMap, i.xz / _DryTile).rgb;
                half3 c = lerp(grass, dry, i.mix.g) * (1 - i.mix.b * 0.35h) * _BaseColor.rgb;

                half2 d = SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, i.xz / _DryTile).ag * 2 - 1;
                half3 n = normalize(i.normal + half3(d.x, 0, d.y) * 0.5h);
                Light sun = GetMainLight();
                c *= sun.color * (saturate(dot(n, sun.direction)) * i.mix.a) + i.ambient.rgb;
                return half4(MixFog(c, i.ambient.a) * _BaseColor.a, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
