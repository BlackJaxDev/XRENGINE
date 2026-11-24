// Engine-authored compatibility include for translated Unity programs.
// It gives the shadow caster entry points of Unity's UnityStandardShadow.cginc:
// vertShadowCaster and fragShadowCaster. The pass writes depth only.
// _ALPHATEST_ON clips at _Cutoff. The blended modes clip at an alpha of 0.5,
// because the engine shadow pass has no dither pattern.
#ifndef UNITY_STANDARD_SHADOW_INCLUDED
#define UNITY_STANDARD_SHADOW_INCLUDED

#include "UnityCG.cginc"
#include "UnityStandardConfig.cginc"

#if defined(_ALPHATEST_ON) || defined(_ALPHABLEND_ON) || defined(_ALPHAPREMULTIPLY_ON)
#define XR_STANDARD_SHADOW_USES_ALPHA 1
#endif

float4 _Color;
float _Cutoff;
sampler2D _MainTex;
float4 _MainTex_ST;

struct VertexInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float2 uv0 : TEXCOORD0;
};

struct VertexOutputShadowCaster
{
    float2 tex : TEXCOORD1;
};

void vertShadowCaster(VertexInput v, out float4 opos : SV_POSITION, out VertexOutputShadowCaster o)
{
    opos = UnityObjectToClipPos(v.vertex);
    o.tex = TRANSFORM_TEX(v.uv0, _MainTex);
}

float4 fragShadowCaster(float4 vpos : SV_POSITION, VertexOutputShadowCaster i) : SV_Target
{
#if defined(XR_STANDARD_SHADOW_USES_ALPHA)
    float alpha = tex2D(_MainTex, i.tex).a * _Color.a;
#if defined(_ALPHATEST_ON)
    clip(alpha - _Cutoff);
#else
    clip(alpha - 0.5);
#endif
#endif
    return 0;
}

#endif // UNITY_STANDARD_SHADOW_INCLUDED
