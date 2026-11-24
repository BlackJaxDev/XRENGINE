// Synthetic test shader for the shader translator. Written for the engine tests.
// It uses the forward base, forward add, and shadow caster passes of the built-in pipeline.
Shader "XRTests/Translation Toon Lit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _ShadowMask ("Shadow Mask", 2D) = "white" {}
        _ShadeColor ("Shade Color", Color) = (0.6, 0.55, 0.7, 1)
        _ShadeStep ("Shade Step", Range(0, 1)) = 0.5
        _ShadeSoftness ("Shade Softness", Range(0.001, 1)) = 0.05
        _RimColor ("Rim Color", Color) = (1, 1, 1, 1)
        _RimPower ("Rim Power", Range(0.5, 8)) = 4
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Int) = 0
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #include "Lighting.cginc"
    #include "AutoLight.cginc"

    UNITY_DECLARE_TEX2D(_MainTex);
    UNITY_DECLARE_TEX2D_NOSAMPLER(_ShadowMask);
    sampler2D _BumpMap;
    float4 _MainTex_ST;
    float4 _Color;
    float4 _ShadeColor;
    float4 _RimColor;
    float _ShadeStep;
    float _ShadeSoftness;
    float _RimPower;
    float _BumpScale;
    float _Cutoff;

    struct appdata
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float4 tangent : TANGENT;
        float2 uv : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float3 worldPos : TEXCOORD1;
        float3 worldNormal : TEXCOORD2;
        float4 worldTangent : TEXCOORD3;
        UNITY_LIGHTING_COORDS(4, 5)
        UNITY_VERTEX_OUTPUT_STEREO
    };

    v2f vert(appdata v)
    {
        v2f o;
        UNITY_INITIALIZE_OUTPUT(v2f, o);
        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
        o.worldNormal = UnityObjectToWorldNormal(v.normal);
        o.worldTangent = float4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w);
        UNITY_TRANSFER_LIGHTING(o, v.uv);
        return o;
    }

    float3 SurfaceNormal(v2f i, float facing)
    {
        float3 normal = normalize(i.worldNormal) * facing;
    #ifdef _NORMALMAP
        float3 tangent = normalize(i.worldTangent.xyz);
        float3 binormal = cross(normal, tangent) * i.worldTangent.w;
        float3 tangentNormal = UnpackScaleNormal(tex2D(_BumpMap, i.uv), _BumpScale);
        normal = normalize(tangentNormal.x * tangent + tangentNormal.y * binormal + tangentNormal.z * normal);
    #endif
        return normal;
    }

    float4 ShadeToon(v2f i, float facing, bool includeAmbient)
    {
        float4 albedo = UNITY_SAMPLE_TEX2D(_MainTex, i.uv) * _Color;
        clip(albedo.a - _Cutoff);
        float3 normal = SurfaceNormal(i, facing);
        float3 lightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));
        float3 viewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));
        UNITY_LIGHT_ATTENUATION(attenuation, i, i.worldPos);
        float mask = UNITY_SAMPLE_TEX2D_SAMPLER(_ShadowMask, _MainTex, i.uv).r;
        float ndotl = dot(normal, lightDir) * 0.5 + 0.5;
        float lit = smoothstep(_ShadeStep - _ShadeSoftness, _ShadeStep + _ShadeSoftness, ndotl * attenuation * mask);
        float3 diffuse = lerp(_ShadeColor.rgb, float3(1, 1, 1), lit) * _LightColor0.rgb;
        float rim = pow(1.0 - saturate(dot(normal, viewDir)), _RimPower) * lit;
        float3 color = albedo.rgb * diffuse + rim * _RimColor.rgb * _LightColor0.rgb;
        if (includeAmbient)
            color += albedo.rgb * ShadeSH9(float4(normal, 1.0));
        return float4(color, albedo.a);
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Cull [_Cull]

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            ZWrite On

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment fragBase
            #pragma multi_compile_fwdbase
            #pragma shader_feature_local_fragment _NORMALMAP

            float4 fragBase(v2f i, float facing : VFACE) : SV_Target
            {
                return ShadeToon(i, facing > 0 ? 1.0 : -1.0, true);
            }
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            ZWrite Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment fragAdd
            #pragma multi_compile_fwdadd_fullshadows
            #pragma shader_feature_local_fragment _NORMALMAP

            float4 fragAdd(v2f i, float facing : VFACE) : SV_Target
            {
                return float4(ShadeToon(i, facing > 0 ? 1.0 : -1.0, false).rgb, 0.0);
            }
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            CGPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_shadowcaster

            struct v2fShadow
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
            };

            v2fShadow vertShadow(appdata_base v)
            {
                v2fShadow o;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            float4 fragShadow(v2fShadow i) : SV_Target
            {
                clip(UNITY_SAMPLE_TEX2D(_MainTex, i.uv).a * _Color.a - _Cutoff);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }

        Pass
        {
            Name "META"
            Tags { "LightMode" = "Meta" }
            Cull Off
        }
    }

    Fallback "Legacy Shaders/VertexLit"
}
