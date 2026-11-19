// Engine-authored compatibility include for translated Unity programs.
// It gives the public structs, functions, and macros of Unity's UnityCG.cginc
// with engine data behind them. The math follows the documented behavior of
// each function; it is not Unity source.
#ifndef UNITY_CG_INCLUDED
#define UNITY_CG_INCLUDED

#include "UnityShaderVariables.cginc"
#include "UnityShaderUtilities.cginc"
#include "UnityInstancing.cginc"

#define UNITY_PI 3.14159265359
#define UNITY_TWO_PI 6.28318530718
#define UNITY_FOUR_PI 12.56637061436
#define UNITY_INV_PI 0.31830988618
#define UNITY_INV_TWO_PI 0.15915494309
#define UNITY_INV_FOUR_PI 0.07957747155
#define UNITY_HALF_PI 1.57079632679
#define UNITY_INV_HALF_PI 0.636619772367

#ifdef UNITY_COLORSPACE_GAMMA
#define unity_ColorSpaceGrey float4(0.5, 0.5, 0.5, 0.5)
#define unity_ColorSpaceDouble float4(2.0, 2.0, 2.0, 2.0)
#define unity_ColorSpaceDielectricSpec float4(0.220916301, 0.220916301, 0.220916301, 1.0 - 0.220916301)
#define unity_ColorSpaceLuminance float4(0.22, 0.707, 0.071, 0.0)
#else
#define unity_ColorSpaceGrey float4(0.214041144, 0.214041144, 0.214041144, 0.5)
#define unity_ColorSpaceDouble float4(4.59479380, 4.59479380, 4.59479380, 2.0)
#define unity_ColorSpaceDielectricSpec float4(0.04, 0.04, 0.04, 1.0 - 0.04)
#define unity_ColorSpaceLuminance float4(0.0396819152, 0.458021790, 0.00609653955, 1.0)
#endif

// A macro, not a function, so that static const initializers that use it stay compile-time constants.
#ifdef UNITY_COLORSPACE_GAMMA
#define IsGammaSpace() true
#else
#define IsGammaSpace() false
#endif

struct appdata_base
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct appdata_tan
{
    float4 vertex : POSITION;
    float4 tangent : TANGENT;
    float3 normal : NORMAL;
    float4 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct appdata_full
{
    float4 vertex : POSITION;
    float4 tangent : TANGENT;
    float3 normal : NORMAL;
    float4 texcoord : TEXCOORD0;
    float4 texcoord1 : TEXCOORD1;
    float4 texcoord2 : TEXCOORD2;
    float4 texcoord3 : TEXCOORD3;
    float4 color : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct appdata_img
{
    float4 vertex : POSITION;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f_img
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

#define TRANSFORM_TEX(tex, name) ((tex).xy * name##_ST.xy + name##_ST.zw)

float2 MultiplyUV(float4x4 textureMatrix, float2 uv) { return mul(textureMatrix, float4(uv, 0.0, 0.0)).xy; }

// Full-screen image effect vertex stage.
v2f_img vert_img(appdata_img v)
{
    v2f_img o = (v2f_img)0;
    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv = v.texcoord;
    return o;
}

// Space transforms.
float3 UnityObjectToViewPos(float3 position) { return mul(UNITY_MATRIX_V, mul(unity_ObjectToWorld, float4(position, 1.0))).xyz; }
float3 UnityObjectToViewPos(float4 position) { return UnityObjectToViewPos(position.xyz); }
float4 UnityWorldToClipPos(float3 position) { return mul(UNITY_MATRIX_VP, float4(position, 1.0)); }
float4 UnityViewToClipPos(float3 position) { return mul(UNITY_MATRIX_P, float4(position, 1.0)); }

// Projects a view-space direction. Only the upper-left part of the projection matrix applies.
float2 TransformViewToProjection(float2 direction)
{
    float4x4 projection = UNITY_MATRIX_P;
    return float2(dot(projection[0].xy, direction), dot(projection[1].xy, direction));
}

float3 TransformViewToProjection(float3 direction)
{
    float4x4 projection = UNITY_MATRIX_P;
    return float3(dot(projection[0].xyz, direction), dot(projection[1].xyz, direction), dot(projection[2].xyz, direction));
}
float3 UnityWorldToViewPos(float3 position) { return mul(UNITY_MATRIX_V, float4(position, 1.0)).xyz; }
float3 UnityObjectToWorldDir(float3 direction) { return normalize(mul((float3x3)unity_ObjectToWorld, direction)); }
float3 UnityWorldToObjectDir(float3 direction) { return normalize(mul((float3x3)unity_WorldToObject, direction)); }

float3 UnityObjectToWorldNormal(float3 normal)
{
    // The inverse transpose of the object-to-world matrix keeps normals correct under non-uniform scale.
    return normalize(mul(normal, (float3x3)unity_WorldToObject));
}

float3 UnityWorldSpaceViewDir(float3 worldPosition) { return _WorldSpaceCameraPos.xyz - worldPosition; }
float3 WorldSpaceViewDir(float4 objectPosition) { return UnityWorldSpaceViewDir(mul(unity_ObjectToWorld, objectPosition).xyz); }
float3 ObjSpaceViewDir(float4 objectPosition) { return mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz - objectPosition.xyz; }

float3 UnityWorldSpaceLightDir(float3 worldPosition)
{
    float4 light = _WorldSpaceLightPos0;
    return light.xyz - worldPosition * light.w;
}

float3 WorldSpaceLightDir(float4 objectPosition) { return UnityWorldSpaceLightDir(mul(unity_ObjectToWorld, objectPosition).xyz); }
float3 ObjSpaceLightDir(float4 objectPosition)
{
    float4 light = _WorldSpaceLightPos0;
    float3 objectLight = mul(unity_WorldToObject, light).xyz;
    return objectLight - objectPosition.xyz * light.w;
}

// Screen positions. Clip space has its origin at the bottom (UNITY_UV_STARTS_AT_TOP is 0).
float4 ComputeNonStereoScreenPos(float4 clipPosition)
{
    float4 screen = clipPosition * 0.5;
    screen.xy = float2(screen.x, screen.y * _ProjectionParams.x) + screen.w;
    screen.zw = clipPosition.zw;
    return screen;
}

float4 ComputeScreenPos(float4 clipPosition) { return ComputeNonStereoScreenPos(clipPosition); }
float4 ComputeGrabScreenPos(float4 clipPosition) { return ComputeNonStereoScreenPos(clipPosition); }
float2 UnityStereoTransformScreenSpaceTex(float2 uv) { return uv; }
float4 UnityStereoTransformScreenSpaceTex(float4 uv) { return uv; }
float2 UnityStereoScreenSpaceUVAdjust(float2 uv, float4 scaleAndOffset) { return uv * scaleAndOffset.xy + scaleAndOffset.zw; }
#define UnityStereoClamp(uv) (uv)
#define TransformStereoScreenSpaceTex(uv, w) (uv)
#define COMPUTE_EYEDEPTH(o) o = -UnityObjectToViewPos(v.vertex).z
#define COMPUTE_VIEW_NORMAL normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal))

// Depth.
float Linear01Depth(float z) { return 1.0 / (_ZBufferParams.x * z + _ZBufferParams.y); }
float LinearEyeDepth(float z) { return 1.0 / (_ZBufferParams.z * z + _ZBufferParams.w); }

// Color.
float Luminance(float3 color) { return dot(color, unity_ColorSpaceLuminance.rgb); }
float LinearRgbToLuminance(float3 linearColor) { return dot(linearColor, float3(0.2126729, 0.7151522, 0.0721750)); }

float GammaToLinearSpaceExact(float value)
{
    if (value <= 0.04045)
        return value / 12.92;
    if (value < 1.0)
        return pow((value + 0.055) / 1.055, 2.4);
    return pow(value, 2.2);
}

float LinearToGammaSpaceExact(float value)
{
    if (value <= 0.0)
        return 0.0;
    if (value <= 0.0031308)
        return 12.92 * value;
    if (value < 1.0)
        return 1.055 * pow(value, 0.4166667) - 0.055;
    return pow(value, 0.45454545);
}

float3 GammaToLinearSpace(float3 color)
{
    // Polynomial fit of the sRGB curve, as accurate as the exact form for 8-bit input.
    return color * (color * (color * 0.305306011 + 0.682171111) + 0.012522878);
}

float3 LinearToGammaSpace(float3 color)
{
    color = max(color, float3(0.0, 0.0, 0.0));
    return max(1.055 * pow(color, 0.416666667) - 0.055, 0.0);
}

// Normals.
float3 UnpackNormalmapRGorAG(float4 packedNormal)
{
    // DXT5nm stores X in alpha; a plain RGB map stores X in red. Multiplying covers both.
    packedNormal.x *= packedNormal.w;
    float3 normal;
    normal.xy = packedNormal.xy * 2.0 - 1.0;
    normal.z = sqrt(1.0 - saturate(dot(normal.xy, normal.xy)));
    return normal;
}

float3 UnpackNormal(float4 packedNormal) { return UnpackNormalmapRGorAG(packedNormal); }

float3 UnpackNormalWithScale(float4 packedNormal, float scale)
{
    packedNormal.x *= packedNormal.w;
    float3 normal;
    normal.xy = (packedNormal.xy * 2.0 - 1.0) * scale;
    normal.z = sqrt(1.0 - saturate(dot(normal.xy, normal.xy)));
    return normal;
}

float3 UnpackScaleNormal(float4 packedNormal, float scale) { return UnpackNormalWithScale(packedNormal, scale); }

// Packing.
float4 EncodeFloatRGBA(float value)
{
    float4 encoded = frac(float4(1.0, 255.0, 65025.0, 16581375.0) * value);
    encoded -= encoded.yzww * float4(1.0 / 255.0, 1.0 / 255.0, 1.0 / 255.0, 0.0);
    return encoded;
}

float DecodeFloatRGBA(float4 encoded) { return dot(encoded, float4(1.0, 1.0 / 255.0, 1.0 / 65025.0, 1.0 / 16581375.0)); }

float2 EncodeFloatRG(float value)
{
    float2 encoded = frac(float2(1.0, 255.0) * value);
    encoded.x -= encoded.y * (1.0 / 255.0);
    return encoded;
}

float DecodeFloatRG(float2 encoded) { return dot(encoded, float2(1.0, 1.0 / 255.0)); }

float3 DecodeHDR(float4 data, float4 decodeInstructions)
{
    float alpha = decodeInstructions.w * (data.a - 1.0) + 1.0;
    return (decodeInstructions.x * pow(alpha, decodeInstructions.y)) * data.rgb;
}

float3 DecodeLightmap(float4 color) { return color.rgb; }

// View-space normals packed in two channels with a stereographic projection.
float2 EncodeViewNormalStereo(float3 normal)
{
    const float scale = 1.7777;
    float2 encoded = normal.xy / (normal.z + 1.0);
    encoded /= scale;
    return encoded * 0.5 + 0.5;
}

float3 DecodeViewNormalStereo(float4 encoded)
{
    const float scale = 1.7777;
    float3 projected = float3(encoded.xy * (2.0 * scale) - scale, 1.0);
    float g = 2.0 / dot(projected.xyz, projected.xyz);
    return float3(g * projected.xy, g - 1.0);
}

float4 EncodeDepthNormal(float depth, float3 normal) { return float4(EncodeViewNormalStereo(normal), EncodeFloatRG(depth)); }

void DecodeDepthNormal(float4 encoded, out float depth, out float3 normal)
{
    depth = DecodeFloatRG(encoded.zw);
    normal = DecodeViewNormalStereo(encoded);
}

// Ambient spherical harmonics. The engine supplies only the constant band.
float3 SHEvalLinearL0L1(float4 normal)
{
    return float3(dot(unity_SHAr, normal), dot(unity_SHAg, normal), dot(unity_SHAb, normal));
}

float3 SHEvalLinearL2(float4 normal) { return float3(0.0, 0.0, 0.0); }
float3 ShadeSH9(float4 normal) { return max(float3(0.0, 0.0, 0.0), SHEvalLinearL0L1(normal) + SHEvalLinearL2(normal)); }
float3 ShadeSH3Order(float4 normal) { return SHEvalLinearL2(normal); }

float3 Shade4PointLights(
    float4 lightPosX, float4 lightPosY, float4 lightPosZ,
    float3 lightColor0, float3 lightColor1, float3 lightColor2, float3 lightColor3,
    float4 lightAttenSq, float3 position, float3 normal)
{
    // Translated passes receive extra lights through the forward light loop, not vertex lights.
    return float3(0.0, 0.0, 0.0);
}

float3 ShadeVertexLights(float4 vertex, float3 normal) { return ShadeSH9(float4(UnityObjectToWorldNormal(normal), 1.0)); }

// Fog. Translated passes leave fog to the engine.
#define UNITY_FOG_COORDS(idx)
#define UNITY_FOG_COORDS_PACKED(idx, vectype)
#define UNITY_TRANSFER_FOG(o, outpos)
#define UNITY_TRANSFER_FOG_COMBINED_WITH_TSPACE(o, outpos)
#define UNITY_TRANSFER_FOG_COMBINED_WITH_WORLD_POS(o, outpos)
#define UNITY_APPLY_FOG(coord, col)
#define UNITY_APPLY_FOG_COLOR(coord, col, fogCol)
#define UNITY_EXTRACT_FOG(name)
#define UNITY_EXTRACT_FOG_FROM_TSPACE(name)
#define UNITY_EXTRACT_FOG_FROM_WORLD_POS(name)
#define UNITY_CALC_FOG_FACTOR(coord)
#define UNITY_CALC_FOG_FACTOR_RAW(coord)
#define UNITY_FOG_LERP_COLOR(col, fogCol, fogFac)

// Shadow caster. Depth-only passes write clip position; the fragment outputs zero.
float4 UnityClipSpaceShadowCasterPos(float4 vertex, float3 normal) { return UnityObjectToClipPos(vertex.xyz); }
float4 UnityClipSpaceShadowCasterPos(float3 vertex, float3 normal) { return UnityObjectToClipPos(vertex); }
float4 UnityApplyLinearShadowBias(float4 clipPosition) { return clipPosition; }
#define V2F_SHADOW_CASTER_NOPOS
#define V2F_SHADOW_CASTER float4 pos : SV_POSITION
#define V2F_SHADOW_CASTER_NOPOS_IS_EMPTY
#define TRANSFER_SHADOW_CASTER_NOPOS(o, opos) opos = UnityObjectToClipPos(v.vertex.xyz);
#define TRANSFER_SHADOW_CASTER_NOPOS_LEGACY(o, opos) opos = UnityObjectToClipPos(v.vertex.xyz);
#define TRANSFER_SHADOW_CASTER(o) o.pos = UnityObjectToClipPos(v.vertex.xyz);
#define TRANSFER_SHADOW_CASTER_NORMALOFFSET(o) o.pos = UnityClipSpaceShadowCasterPos(v.vertex, v.normal);
#define SHADOW_CASTER_FRAGMENT(i) return 0;

// Miscellaneous.
#define UNITY_OPAQUE_ALPHA(outputAlpha) outputAlpha = 1.0
#define UNITY_OUTPUT_DEPTH(i) return 0
#define UNITY_TRANSFER_DEPTH(oo)
#define UNITY_EXTRACT_TBN_0(name)
#define UNITY_EXTRACT_TBN_1(name)
#define UNITY_EXTRACT_TBN_2(name)
#define UNITY_DITHER_CROSSFADE_COORDS
#define UNITY_APPLY_DITHER_CROSSFADE(vpos)

#endif // UNITY_CG_INCLUDED
