// Forward Lighting Snippet - PBR Forward+ lighting with probe-based ambient
// Requires camera matrices for screen-space contact shadows; the forward light
// upload provides them even when a material only requests the Lights binding set.

#pragma snippet "LightStructs"
#pragma snippet "LightAttenuation"
#pragma snippet "ShadowSampling"

const float PI = 3.14159265359;
const float MAX_REFLECTION_LOD = 4.0;
const float XRENGINE_MIN_FORWARD_AMBIENT_FALLBACK = 0.08;
const int XRENGINE_SHADOW_FALLBACK_LIT = 1;
const int XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY = 2;
const int XRENGINE_SHADOW_FALLBACK_LEGACY = 5;

uniform vec3 GlobalAmbient;
uniform float Roughness = 0.9;
uniform float Metallic = 0.0;
uniform float Emission = 0.0;
uniform bool AmbientOcclusionMultiBounce;
uniform bool SpecularOcclusionEnabled;
uniform bool ForwardPbrResourcesEnabled;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 ViewProjectionMatrix;
uniform mat4 LeftEyeViewMatrix;
uniform mat4 RightEyeViewMatrix;
uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;
uniform mat4 LeftEyeViewProjectionMatrix;
uniform mat4 RightEyeViewProjectionMatrix;

#if !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
#define XRENGINE_FORWARD_HAS_FRAGMENT_VIEW_INDEX 1
layout(location = 22) in float FragViewIndex;
#endif

#ifndef XRENGINE_DEPTH_MODE_UNIFORM
#define XRENGINE_DEPTH_MODE_UNIFORM
uniform int DepthMode;
#endif

#ifndef XRENGINE_SCREEN_SIZE_UNIFORMS
#define XRENGINE_SCREEN_SIZE_UNIFORMS
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif

layout(binding = 6) uniform sampler2D BRDF;
layout(binding = 7) uniform sampler2DArray IrradianceArray;
layout(binding = 8) uniform sampler2DArray PrefilterArray;

layout(std430, binding = 0) readonly buffer LightProbePositions
{
    vec4 ProbePositions[];
};

layout(std430, binding = 1) readonly buffer LightProbeTetrahedra
{
    vec4 TetraIndices[];
};

struct ProbeParam
{
    vec4 InfluenceInner;
    vec4 InfluenceOuter;
    vec4 InfluenceOffsetShape;
    vec4 ProxyCenterEnable;
    vec4 ProxyHalfExtents;
    vec4 ProxyRotation;
};

layout(std430, binding = 2) readonly buffer LightProbeParameters
{
    ProbeParam ProbeParams[];
};

struct ProbeGridCell
{
    ivec4 OffsetCount;
    ivec4 FallbackIndices;
};

layout(std430, binding = 3) readonly buffer LightProbeGridCells
{
    ProbeGridCell GridCells[];
};

layout(std430, binding = 4) readonly buffer LightProbeGridIndices
{
    int CellTetraIndices[];
};

uniform int ProbeCount;
uniform int TetraCount;
uniform ivec3 ProbeGridDims;
uniform vec3 ProbeGridOrigin;
uniform float ProbeGridCellSize;
uniform bool UseProbeGrid;

// Directional shadow maps use fixed high texture units to avoid collision with material textures.
const int XRENGINE_MAX_DIRECTIONAL_SHADOW_RECORDS = XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS * XRENGINE_MAX_CASCADES;
layout(binding = 15) uniform sampler2D DirectionalShadowMaps[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
layout(binding = 17) uniform sampler2DArray DirectionalShadowMapArrays[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform int DirectionalShadowMapEnabled[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform int DirectionalUseCascadedShadows[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform int DirectionalShadowMapEncoding[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform vec4 DirectionalShadowMomentParams0[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform vec4 DirectionalShadowMomentFilterParams[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform ivec4 ShadowPackedI0 = ivec4(8, 8, 5, 2); // blocker samples, filter samples, vogel taps, soft mode
uniform ivec4 ShadowPackedI1 = ivec4(1, 1, 16, 0); // cascades enabled, contact enabled, contact samples, reserved
uniform vec4 ShadowParams0 = vec4(0.035, 1.221, 0.00001, 0.004); // base, exponent, bias min, bias max
uniform vec4 ShadowParams1 = vec4(0.0012, 0.01, 0.001, 0.015); // filter radius, blocker radius, min penumbra, max penumbra
uniform vec4 ShadowParams2 = vec4(1.2, 1.0, 2.0, 10.0); // source radius, contact distance, contact thickness, contact fade start
uniform vec4 ShadowParams3 = vec4(40.0, 0.0, 1.0, 0.0); // contact fade end, normal offset, jitter, reserved
uniform vec4 ShadowBiasParams = vec4(1.0, 2.0, 1.0, 0.0); // depth texels, slope texels, normal texels, reserved
uniform vec4 ShadowBiasProjectionParams = vec4(0.0, 0.0, 0.0, 0.0); // constant depth bias, normal offset, world texel size, depth range
uniform vec4 DirectionalShadowBiasProjectionParams[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform int DirectionalShadowAtlasEnabled[XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS];
uniform ivec4 DirectionalShadowAtlasPacked0[XRENGINE_MAX_DIRECTIONAL_SHADOW_RECORDS]; // enabled, page, fallback, record index
uniform vec4 DirectionalShadowAtlasParams0[XRENGINE_MAX_DIRECTIONAL_SHADOW_RECORDS];  // uv scale/bias
uniform vec4 DirectionalShadowAtlasParams1[XRENGINE_MAX_DIRECTIONAL_SHADOW_RECORDS];  // near, far, local texel size, requested/allocated scale

#define ShadowBlockerSamples ShadowPackedI0.x
#define ShadowFilterSamples ShadowPackedI0.y
#define ShadowVogelTapCount ShadowPackedI0.z
#define SoftShadowMode ShadowPackedI0.w
#define EnableCascadedShadows (ShadowPackedI1.x != 0)
#define EnableContactShadows (ShadowPackedI1.y != 0)
#define ContactShadowSamples ShadowPackedI1.z
#define ShadowBase ShadowParams0.x
#define ShadowMult ShadowParams0.y
#define ShadowBiasMin ShadowParams0.z
#define ShadowBiasMax ShadowParams0.w
#define ShadowFilterRadius ShadowParams1.x
#define ShadowBlockerSearchRadius ShadowParams1.y
#define ShadowMinPenumbra ShadowParams1.z
#define ShadowMaxPenumbra ShadowParams1.w
#define LightSourceRadius ShadowParams2.x
#define ContactShadowDistance ShadowParams2.y
#define ContactShadowThickness ShadowParams2.z
#define ContactShadowFadeStart ShadowParams2.w
#define ContactShadowFadeEnd ShadowParams3.x
#define ContactShadowNormalOffset ShadowParams3.y
#define ContactShadowJitterStrength ShadowParams3.z
#define ShadowDepthBiasTexels ShadowBiasParams.x
#define ShadowSlopeBiasTexels ShadowBiasParams.y
#define ShadowNormalBiasTexels ShadowBiasParams.z

uniform int DirLightCount; 
uniform int SpotLightCount;
uniform int PointLightCount;

layout(std430, binding = 22) readonly buffer ForwardDirectionalLightsBuffer
{
    DirLight DirectionalLights[];
};

layout(std430, binding = 23) readonly buffer ForwardPointLightsBuffer
{
    PointLight PointLights[];
};

layout(std430, binding = 26) readonly buffer ForwardSpotLightsBuffer
{
    SpotLight SpotLights[];
};

const int XRENGINE_MAX_FORWARD_POINT_SHADOW_SLOTS = 4;
const int XRENGINE_MAX_FORWARD_SPOT_SHADOW_SLOTS = 4;
const int XRENGINE_POINT_SHADOW_FACE_COUNT = 6;

layout(binding = 19) uniform samplerCube PointLightShadowMaps[XRENGINE_MAX_FORWARD_POINT_SHADOW_SLOTS];
layout(binding = 23) uniform sampler2D SpotLightShadowMaps[XRENGINE_MAX_FORWARD_SPOT_SHADOW_SLOTS];
layout(binding = 28) uniform sampler2D ForwardContactDepthView;
layout(binding = 29) uniform sampler2D ForwardContactNormalView;
layout(binding = 30) uniform sampler2DArray ForwardContactDepthViewArray;
layout(binding = 31) uniform sampler2DArray ForwardContactNormalViewArray;
layout(binding = 9) uniform sampler2DArray DirectionalShadowAtlas;
layout(binding = 32) uniform sampler2DArray SpotLightShadowAtlas;
layout(binding = 34) uniform sampler2DArray PointLightShadowAtlas;
uniform bool ForwardContactShadowsEnabled = false;
uniform bool ForwardContactShadowsArrayEnabled = false;

struct ForwardPointShadowData
{
    ivec4 Packed0; // slot, filter samples, blocker samples, vogel taps
    ivec4 Packed1; // soft mode, debug mode, contact enabled, contact samples
    vec4 Params0;  // near plane, far plane, shadow base, shadow exponent
    vec4 Params1;  // bias min, bias max, filter radius, blocker radius
    vec4 Params2;  // min penumbra, max penumbra, source radius, contact distance
    vec4 Params3;  // contact thickness, fade start, fade end, normal offset
    vec4 Params4;  // contact jitter, reserved
    vec4 Params5;  // depth bias texels, slope bias texels, normal bias texels, reserved
    ivec4 Packed2; // encoding, use moment mipmaps, moment blur radius texels, moment blur passes
    vec4 Params6;  // moment min variance, bleed reduction, positive exponent, negative exponent
    ivec4 AtlasPacked0[XRENGINE_POINT_SHADOW_FACE_COUNT]; // enabled, page, fallback, record index
    vec4 AtlasParams0[XRENGINE_POINT_SHADOW_FACE_COUNT];  // uv scale/bias
    vec4 AtlasParams1[XRENGINE_POINT_SHADOW_FACE_COUNT];  // near, far, local texel size, requested/allocated scale
};

struct ForwardSpotShadowData
{
    ivec4 Packed0; // slot, filter samples, blocker samples, vogel taps
    ivec4 Packed1; // soft mode, debug mode, contact enabled, contact samples
    vec4 Params0;  // shadow base, shadow exponent, bias min, bias max
    vec4 Params1;  // filter radius, blocker radius, min penumbra, max penumbra
    vec4 Params2;  // source radius, contact distance, contact thickness, fade start
    vec4 Params3;  // fade end, normal offset, jitter, reserved
    ivec4 Packed2; // encoding, use moment mipmaps, moment blur radius texels, moment blur passes
    vec4 Params4;  // moment min variance, bleed reduction, positive exponent, negative exponent
    vec4 Params5;  // shadow near, shadow far, moment mip bias, reserved
    ivec4 AtlasPacked0; // enabled, page, fallback, record index
    vec4 AtlasParams0;  // uv scale/bias
    vec4 AtlasParams1;  // near, far, local texel size, requested/allocated scale
    vec4 Params6;  // depth bias texels, slope bias texels, normal bias texels, reserved
};

layout(std430, binding = 27) readonly buffer ForwardPointShadowMetadataBuffer
{
    ForwardPointShadowData PointLightShadows[];
};

layout(std430, binding = 28) readonly buffer ForwardSpotShadowMetadataBuffer
{
    ForwardSpotShadowData SpotLightShadows[];
};

// Forward+ tiled light culling uniforms
uniform bool ForwardPlusEnabled;
uniform vec2 ForwardPlusScreenSize;
uniform int ForwardPlusTileSize;
// Precomputed tile counts (ceil(screen / tileSize)) — supplied by the engine
// so shaders don't have to recompute them per fragment.
uniform int ForwardPlusTileCountX;
uniform int ForwardPlusTileCountY;
uniform int ForwardPlusMaxLightsPerTile;
uniform int ForwardPlusEyeCount;
#ifndef XRENGINE_SCREEN_ORIGIN_UNIFORM
#define XRENGINE_SCREEN_ORIGIN_UNIFORM
uniform vec2 ScreenOrigin;
#endif

struct ForwardPlusLocalLight
{
    vec4 PositionWS;
    vec4 DirectionWS_Exponent;
    vec4 Color_Type;     // rgb=color, w=type (0=point, 1=spot)
    vec4 Params;         // x=radius, y=brightness, z=diffuseIntensity, w=legacy source index
    vec4 SpotAngles;     // x=innerCutoff, y=outerCutoff
    ivec4 Indices;       // x=source light index, y=shadow record index, z=casts shadows
};

layout(std430, binding = 20) readonly buffer ForwardPlusLocalLightsBuffer
{
    ForwardPlusLocalLight ForwardPlusLocalLights[];
};

layout(std430, binding = 21) readonly buffer ForwardPlusVisibleIndicesBuffer
{
    int ForwardPlusVisibleIndices[];
};

int XRENGINE_ForwardShadowDebugModeActive = 0;
vec3 XRENGINE_ForwardShadowDebugColor = vec3(0.0);
bool XRENGINE_ForwardLightingContextInitialized = false;
int XRENGINE_ResolvedForwardViewIndex = 0;
mat4 XRENGINE_ResolvedForwardViewMatrix = mat4(1.0);
mat4 XRENGINE_ResolvedForwardInverseViewMatrix = mat4(1.0);
mat4 XRENGINE_ResolvedForwardInverseProjMatrix = mat4(1.0);
mat4 XRENGINE_ResolvedForwardProjMatrix = mat4(1.0);
mat4 XRENGINE_ResolvedForwardViewProjectionMatrix = mat4(1.0);
vec3 XRENGINE_ResolvedForwardCameraPosition = vec3(0.0);
vec3 XRENGINE_ResolvedForwardFragPos = vec3(0.0);
float XRENGINE_ResolvedForwardViewDepth = 0.0;
int XRENGINE_DirectionalShadowAtlasLayerCount = 0;
int XRENGINE_PointShadowAtlasLayerCount = 0;
int XRENGINE_SpotShadowAtlasLayerCount = 0;
ivec4 XRENGINE_ResolvedShadowPackedI0 = ivec4(0);
ivec4 XRENGINE_ResolvedShadowPackedI1 = ivec4(0);
vec4 XRENGINE_ResolvedShadowParams0 = vec4(0.0);
vec4 XRENGINE_ResolvedShadowParams1 = vec4(0.0);
vec4 XRENGINE_ResolvedShadowParams2 = vec4(0.0);
vec4 XRENGINE_ResolvedShadowParams3 = vec4(0.0);

vec3 XRENGINE_ResolveForwardShadowDebugColor(int debugMode, float lit, float margin)
{
    if (debugMode == 1)
        return vec3(lit);

    if (debugMode == 2)
    {
        float intensity = min(abs(margin) * 20.0, 1.0);
        float clampedLit = clamp(lit, 0.0, 1.0);
        return vec3((1.0 - clampedLit) * intensity, clampedLit * intensity, 0.0);
    }

    return vec3(0.0);
}

void XRENGINE_TrySetForwardShadowDebug(int debugMode, float lit, float margin)
{
    if (debugMode == 0 || XRENGINE_ForwardShadowDebugModeActive != 0)
        return;

    XRENGINE_ForwardShadowDebugModeActive = debugMode;
    XRENGINE_ForwardShadowDebugColor = XRENGINE_ResolveForwardShadowDebugColor(debugMode, lit, margin);
}

vec3 XRENGINE_MultiBounceAO(float ao, vec3 albedo)
{
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    return max(vec3(ao), ((a * ao + b) * ao + c) * ao);
}

float XRENGINE_GTSpecularOcclusion(float NoV, float ao, float roughness)
{
    return clamp(pow(NoV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

float XRENGINE_SpecD_TRGGX(float NoH2, float a2)
{
    float denom = (NoH2 * (a2 - 1.0) + 1.0);
    return a2 / (PI * denom * denom);
}

float XRENGINE_SpecG_SchlickGGX(float NoV, float k)
{
    return NoV / (NoV * (1.0 - k) + k);
}

float XRENGINE_SpecG_Smith(float NoV, float NoL, float k)
{
    return XRENGINE_SpecG_SchlickGGX(NoV, k) * XRENGINE_SpecG_SchlickGGX(NoL, k);
}

vec3 XRENGINE_SpecF_SchlickApprox(float VoH, vec3 F0)
{
    float powTerm = exp2((-5.55473 * VoH - 6.98316) * VoH);
    return F0 + (1.0 - F0) * powTerm;
}

vec3 XRENGINE_SpecF_SchlickRoughnessApprox(float VoH, vec3 F0, float roughness)
{
    float powTerm = exp2((-5.55473 * VoH - 6.98316) * VoH);
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * powTerm;
}

vec2 XRENGINE_EncodeOcta(vec3 dir)
{
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0)
    {
        vec2 signDir = vec2(octDir.x >= 0.0 ? 1.0 : -1.0, octDir.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signDir;
    }

    return uv * 0.5 + 0.5;
}

vec3 XRENGINE_SampleOctaArray(sampler2DArray tex, vec3 dir, float layer)
{
    return texture(tex, vec3(XRENGINE_EncodeOcta(dir), layer)).rgb;
}

vec3 XRENGINE_SampleOctaArrayLod(sampler2DArray tex, vec3 dir, float layer, float lod)
{
    return textureLod(tex, vec3(XRENGINE_EncodeOcta(dir), layer), lod).rgb;
}

int XRENGINE_GetForwardViewIndex()
{
#if defined(XRENGINE_FORWARD_HAS_FRAGMENT_VIEW_INDEX)
    if (ForwardPlusEyeCount <= 1)
        return 0;

    return int(clamp(round(FragViewIndex), 0.0, float(ForwardPlusEyeCount - 1)));
#else
    return 0;
#endif
}

mat4 XRENGINE_GetForwardViewMatrixForIndex(int viewIndex)
{
    if (ForwardPlusEyeCount <= 1)
        return ViewMatrix;

    return viewIndex == 0
        ? LeftEyeViewMatrix
        : RightEyeViewMatrix;
}

mat4 XRENGINE_GetForwardViewMatrix()
{
    return XRENGINE_GetForwardViewMatrixForIndex(XRENGINE_GetForwardViewIndex());
}

mat4 XRENGINE_GetForwardInverseViewMatrixForIndex(int viewIndex)
{
    if (ForwardPlusEyeCount <= 1)
        return InverseViewMatrix;

    return viewIndex == 0
        ? LeftEyeInverseViewMatrix
        : RightEyeInverseViewMatrix;
}

mat4 XRENGINE_GetForwardInverseViewMatrix()
{
    return XRENGINE_GetForwardInverseViewMatrixForIndex(XRENGINE_GetForwardViewIndex());
}

mat4 XRENGINE_GetForwardInverseProjMatrixForIndex(int viewIndex)
{
    if (ForwardPlusEyeCount <= 1)
        return InverseProjMatrix;

    return viewIndex == 0
        ? LeftEyeInverseProjMatrix
        : RightEyeInverseProjMatrix;
}

mat4 XRENGINE_GetForwardInverseProjMatrix()
{
    return XRENGINE_GetForwardInverseProjMatrixForIndex(XRENGINE_GetForwardViewIndex());
}

mat4 XRENGINE_GetForwardProjMatrixForIndex(int viewIndex)
{
    if (ForwardPlusEyeCount <= 1)
        return ProjMatrix;

    return viewIndex == 0
        ? LeftEyeProjMatrix
        : RightEyeProjMatrix;
}

mat4 XRENGINE_GetForwardProjMatrix()
{
    return XRENGINE_GetForwardProjMatrixForIndex(XRENGINE_GetForwardViewIndex());
}

mat4 XRENGINE_GetForwardViewProjectionMatrixForIndex(int viewIndex)
{
    if (ForwardPlusEyeCount <= 1)
        return ViewProjectionMatrix;

    return viewIndex == 0
        ? LeftEyeViewProjectionMatrix
        : RightEyeViewProjectionMatrix;
}

mat4 XRENGINE_GetForwardViewProjectionMatrix()
{
    return XRENGINE_GetForwardViewProjectionMatrixForIndex(XRENGINE_GetForwardViewIndex());
}

vec3 XRENGINE_GetForwardCameraPositionForIndex(int viewIndex)
{
    if (ForwardPlusEyeCount <= 1)
        return InverseViewMatrix[3].xyz;

    return viewIndex == 0
        ? LeftEyeInverseViewMatrix[3].xyz
        : RightEyeInverseViewMatrix[3].xyz;
}

vec3 XRENGINE_GetForwardCameraPosition()
{
    return XRENGINE_GetForwardCameraPositionForIndex(XRENGINE_GetForwardViewIndex());
}

float XRENGINE_ViewDepthFromWorldPosWithViewMatrix(vec3 fragPosWS, mat4 viewMatrix)
{
    vec4 viewPos = viewMatrix * vec4(fragPosWS, 1.0);
    return abs(viewPos.z);
}

float XRENGINE_ViewDepthFromWorldPos(vec3 fragPosWS)
{
    return XRENGINE_ViewDepthFromWorldPosWithViewMatrix(fragPosWS, XRENGINE_GetForwardViewMatrix());
}

int XRENGINE_GetForwardResolvedViewIndex()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedForwardViewIndex
        : XRENGINE_GetForwardViewIndex();
}

mat4 XRENGINE_GetForwardResolvedViewMatrix()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedForwardViewMatrix
        : XRENGINE_GetForwardViewMatrix();
}

mat4 XRENGINE_GetForwardResolvedInverseViewMatrix()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedForwardInverseViewMatrix
        : XRENGINE_GetForwardInverseViewMatrix();
}

mat4 XRENGINE_GetForwardResolvedInverseProjMatrix()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedForwardInverseProjMatrix
        : XRENGINE_GetForwardInverseProjMatrix();
}

mat4 XRENGINE_GetForwardResolvedViewProjectionMatrix()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedForwardViewProjectionMatrix
        : XRENGINE_GetForwardViewProjectionMatrix();
}

vec3 XRENGINE_GetForwardResolvedCameraPosition()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedForwardCameraPosition
        : XRENGINE_GetForwardCameraPosition();
}

float XRENGINE_GetForwardResolvedViewDepth(vec3 fragPosWS)
{
    if (!XRENGINE_ForwardLightingContextInitialized)
        return XRENGINE_ViewDepthFromWorldPos(fragPosWS);

    vec3 delta = abs(fragPosWS - XRENGINE_ResolvedForwardFragPos);
    if (max(delta.x, max(delta.y, delta.z)) <= 1e-5)
        return XRENGINE_ResolvedForwardViewDepth;

    return XRENGINE_ViewDepthFromWorldPosWithViewMatrix(fragPosWS, XRENGINE_ResolvedForwardViewMatrix);
}

int XRENGINE_GetDirectionalShadowAtlasLayerCount()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_DirectionalShadowAtlasLayerCount
        : textureSize(DirectionalShadowAtlas, 0).z;
}

int XRENGINE_GetPointShadowAtlasLayerCount()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_PointShadowAtlasLayerCount
        : textureSize(PointLightShadowAtlas, 0).z;
}

int XRENGINE_GetSpotShadowAtlasLayerCount()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_SpotShadowAtlasLayerCount
        : textureSize(SpotLightShadowAtlas, 0).z;
}

ivec4 XRENGINE_GetForwardShadowPackedI0()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedShadowPackedI0
        : ShadowPackedI0;
}

ivec4 XRENGINE_GetForwardShadowPackedI1()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedShadowPackedI1
        : ShadowPackedI1;
}

vec4 XRENGINE_GetForwardShadowParams0()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedShadowParams0
        : ShadowParams0;
}

vec4 XRENGINE_GetForwardShadowParams1()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedShadowParams1
        : ShadowParams1;
}

vec4 XRENGINE_GetForwardShadowParams2()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedShadowParams2
        : ShadowParams2;
}

vec4 XRENGINE_GetForwardShadowParams3()
{
    return XRENGINE_ForwardLightingContextInitialized
        ? XRENGINE_ResolvedShadowParams3
        : ShadowParams3;
}

void XRENGINE_BeginForwardLightingFragment(vec3 fragPosWS)
{
    int viewIndex = XRENGINE_GetForwardViewIndex();
    XRENGINE_ResolvedForwardViewIndex = viewIndex;
    XRENGINE_ResolvedForwardViewMatrix = XRENGINE_GetForwardViewMatrixForIndex(viewIndex);
    XRENGINE_ResolvedForwardInverseViewMatrix = XRENGINE_GetForwardInverseViewMatrixForIndex(viewIndex);
    XRENGINE_ResolvedForwardInverseProjMatrix = XRENGINE_GetForwardInverseProjMatrixForIndex(viewIndex);
    XRENGINE_ResolvedForwardProjMatrix = XRENGINE_GetForwardProjMatrixForIndex(viewIndex);
    XRENGINE_ResolvedForwardViewProjectionMatrix = XRENGINE_GetForwardViewProjectionMatrixForIndex(viewIndex);
    XRENGINE_ResolvedForwardCameraPosition = XRENGINE_GetForwardCameraPositionForIndex(viewIndex);
    XRENGINE_ResolvedForwardFragPos = fragPosWS;
    XRENGINE_ResolvedForwardViewDepth = XRENGINE_ViewDepthFromWorldPosWithViewMatrix(
        fragPosWS,
        XRENGINE_ResolvedForwardViewMatrix);
    XRENGINE_DirectionalShadowAtlasLayerCount = textureSize(DirectionalShadowAtlas, 0).z;
    XRENGINE_PointShadowAtlasLayerCount = textureSize(PointLightShadowAtlas, 0).z;
    XRENGINE_SpotShadowAtlasLayerCount = textureSize(SpotLightShadowAtlas, 0).z;
    XRENGINE_ResolvedShadowPackedI0 = ShadowPackedI0;
    XRENGINE_ResolvedShadowPackedI1 = ShadowPackedI1;
    XRENGINE_ResolvedShadowParams0 = ShadowParams0;
    XRENGINE_ResolvedShadowParams1 = ShadowParams1;
    XRENGINE_ResolvedShadowParams2 = ShadowParams2;
    XRENGINE_ResolvedShadowParams3 = ShadowParams3;
    XRENGINE_ForwardLightingContextInitialized = true;
}

float XRENGINE_GetLocalShadowBias(float NoL, float shadowBase, float shadowExponent, float minBias, float maxBias)
{
    float baseMapped = max(shadowBase, 0.0) * (1.0 - NoL);
    float mapped = abs(shadowExponent - 1.0) < 1e-3
        ? baseMapped
        : pow(baseMapped, max(shadowExponent, 0.0001));
    return mix(minBias, maxBias, clamp(mapped, 0.0, 1.0));
}

float XRENGINE_GetBiasParamDepthTexels(vec4 biasParams)
{
    return max(biasParams.x, 0.0);
}

float XRENGINE_GetBiasParamSlopeTexels(vec4 biasParams)
{
    return max(biasParams.y, 0.0);
}

float XRENGINE_GetBiasParamNormalTexels(vec4 biasParams)
{
    return max(biasParams.z, 0.0);
}

float XRENGINE_GetPointShadowSampleRadius(samplerCube shadowMap, float filterRadius)
{
    float faceSize = max(float(textureSize(shadowMap, 0).x), 1.0);
    float texelDirectionSpan = 2.0 / faceSize;
    float requestedScale = clamp(filterRadius * 256.0, 0.0, 4.0);
    return texelDirectionSpan * max(1.0, requestedScale);
}

mat3 XRENGINE_QuaternionToMat3(vec4 q)
{
    vec3 q2 = q.xyz + q.xyz;
    vec3 qq = q.xyz * q2;
    float qx2 = q.x * q2.x;
    float qy2 = q.y * q2.y;
    float qz2 = q.z * q2.z;

    vec3 qwq = q.w * q2;
    vec3 m0 = vec3(1.0 - (qy2 + qz2), qq.x + qwq.z, qq.y - qwq.y);
    vec3 m1 = vec3(qq.x - qwq.z, 1.0 - (qx2 + qz2), qq.z + qwq.x);
    vec3 m2 = vec3(qq.y + qwq.y, qq.z - qwq.x, 1.0 - (qx2 + qy2));
    return mat3(m0, m1, m2);
}

float XRENGINE_ComputeInfluenceWeight(int probeIndex, vec3 worldPos)
{
    ProbeParam p = ProbeParams[probeIndex];
    int shape = int(p.InfluenceOffsetShape.w + 0.5);
    vec3 center = ProbePositions[probeIndex].xyz + p.InfluenceOffsetShape.xyz;
    if (shape == 0)
    {
        float inner = p.InfluenceInner.w;
        float outer = max(p.InfluenceOuter.w, inner + 0.0001);
        float dist = length(worldPos - center);
        return smoothstep(outer, inner, dist);
    }

    vec3 inner = p.InfluenceInner.xyz;
    vec3 outer = max(p.InfluenceOuter.xyz, inner + vec3(0.0001));
    vec3 rel = abs(worldPos - center);
    vec3 ndf3 = clamp((rel - inner) / (outer - inner), 0.0, 1.0);
    float ndf = max(ndf3.x, max(ndf3.y, ndf3.z));
    return smoothstep(1.0, 0.0, ndf);
}

vec3 XRENGINE_ApplyParallax(int probeIndex, vec3 dirWS, vec3 worldPos)
{
    ProbeParam p = ProbeParams[probeIndex];
    if (p.ProxyCenterEnable.w < 0.5)
        return dirWS;

    vec3 proxyCenter = ProbePositions[probeIndex].xyz + p.ProxyCenterEnable.xyz;
    vec3 halfExt = max(p.ProxyHalfExtents.xyz, vec3(0.0001));
    mat3 rot = XRENGINE_QuaternionToMat3(p.ProxyRotation);
    mat3 invRot = transpose(rot);

    vec3 rayOrigLS = invRot * (worldPos - proxyCenter);
    vec3 rayDirLS = normalize(invRot * dirWS);

    // Avoid division by zero per-component without a long branch chain.
    vec3 absDir = abs(rayDirLS);
    vec3 signDir = mix(vec3(1.0), sign(rayDirLS), step(vec3(1e-6), absDir));
    vec3 safeDir = signDir * max(absDir, vec3(1e-6));
    vec3 t1 = (halfExt - rayOrigLS) / safeDir;
    vec3 t2 = (-halfExt - rayOrigLS) / safeDir;
    vec3 tmax = max(t1, t2);
    float dist = min(tmax.x, min(tmax.y, tmax.z));
    if (dist <= 0.0 || isnan(dist) || isinf(dist))
        return dirWS;

    vec3 hitLS = rayOrigLS + rayDirLS * dist;
    vec3 hitWS = proxyCenter + rot * hitLS;
    return normalize(hitWS - ProbePositions[probeIndex].xyz);
}

bool XRENGINE_ComputeBarycentric(vec3 p, vec3 a, vec3 b, vec3 c, vec3 d, out vec4 bary)
{
    // Solve for (u, v, w) in m * (u v w)^T = (p - a) using Cramer's rule.
    // This avoids the full mat3 inverse (cofactor expansion) and is several
    // times cheaper. m's columns are (b-a, c-a, d-a).
    vec3 e1 = b - a;
    vec3 e2 = c - a;
    vec3 e3 = d - a;
    vec3 v = p - a;
    vec3 e2xe3 = cross(e2, e3);
    float det = dot(e1, e2xe3);
    float invDet = 1.0 / (abs(det) > 1e-20 ? det : 1e-20);
    float u = dot(v, e2xe3) * invDet;
    float vCoord = dot(e1, cross(v, e3)) * invDet;
    float wCoord = dot(e1, cross(e2, v)) * invDet;
    float w0 = 1.0 - u - vCoord - wCoord;
    bary = vec4(w0, u, vCoord, wCoord);
    return bary.x >= -0.0001 && bary.y >= -0.0001 && bary.z >= -0.0001 && bary.w >= -0.0001;
}

#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
void XRENGINE_ResolveProbeWeights(vec3 worldPos, out vec4 weights, out ivec4 indices)
{
    // Debug-only O(ProbeCount^2) fallback; never enable this in shipping builds.
    weights = vec4(0.0);
    indices = ivec4(-1);

    for (int i = 0; i < TetraCount; ++i)
    {
        ivec4 idx = ivec4(TetraIndices[i]);
        if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
            continue;

        vec4 bary;
        if (XRENGINE_ComputeBarycentric(worldPos, ProbePositions[idx.x].xyz, ProbePositions[idx.y].xyz, ProbePositions[idx.z].xyz, ProbePositions[idx.w].xyz, bary))
        {
            weights = bary;
            indices = idx;
            return;
        }
    }

    float bestDistances[4] = float[](1e20, 1e20, 1e20, 1e20);
    int bestIndices[4] = int[](-1, -1, -1, -1);
    for (int i = 0; i < ProbeCount; ++i)
    {
        float dist = length(worldPos - ProbePositions[i].xyz);
        for (int k = 0; k < 4; ++k)
        {
            if (dist < bestDistances[k])
            {
                for (int s = 3; s > k; --s)
                {
                    bestDistances[s] = bestDistances[s - 1];
                    bestIndices[s] = bestIndices[s - 1];
                }
                bestDistances[k] = dist;
                bestIndices[k] = i;
                break;
            }
        }
    }

    float sum = 0.0;
    for (int k = 0; k < 4; ++k)
    {
        if (bestIndices[k] >= 0)
        {
            float weight = 1.0 / max(bestDistances[k], 0.0001);
            weights[k] = weight;
            indices[k] = bestIndices[k];
            sum += weight;
        }
    }

    if (sum > 0.0)
        weights /= sum;
}
#endif // XRENGINE_PROBE_DEBUG_FALLBACK

void XRENGINE_ResolveProbeWeightsGrid(vec3 worldPos, out vec4 weights, out ivec4 indices, out bool isBarycentric)
{
    weights = vec4(0.0);
    indices = ivec4(-1);
    isBarycentric = false;

    if (ProbeGridDims.x <= 0 || ProbeGridDims.y <= 0 || ProbeGridDims.z <= 0 || ProbeGridCellSize <= 0.0)
        return;

    vec3 rel = (worldPos - ProbeGridOrigin) / ProbeGridCellSize;
    ivec3 cell = clamp(ivec3(floor(rel)), ivec3(0), ProbeGridDims - ivec3(1));
    int flatIndex = cell.x + cell.y * ProbeGridDims.x + cell.z * ProbeGridDims.x * ProbeGridDims.y;
    ProbeGridCell cellData = GridCells[flatIndex];
    ivec2 offsetCount = cellData.OffsetCount.xy;

    if (offsetCount.y > 0)
    {
        for (int i = 0; i < offsetCount.y; ++i)
        {
            int tetraIndex = CellTetraIndices[offsetCount.x + i];
            if (tetraIndex < 0 || tetraIndex >= TetraCount)
                continue;

            ivec4 idx = ivec4(TetraIndices[tetraIndex]);
            if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
                continue;

            vec4 bary;
            if (XRENGINE_ComputeBarycentric(worldPos, ProbePositions[idx.x].xyz, ProbePositions[idx.y].xyz, ProbePositions[idx.z].xyz, ProbePositions[idx.w].xyz, bary))
            {
                weights = bary;
                indices = idx;
                isBarycentric = true;
                return;
            }
        }
    }

    float sum = 0.0;
    for (int k = 0; k < 4; ++k)
    {
        int probeIndex = cellData.FallbackIndices[k];
        if (probeIndex < 0 || probeIndex >= ProbeCount)
            continue;

        float dist = length(worldPos - ProbePositions[probeIndex].xyz);
        float weight = 1.0 / max(dist, 0.0001);
        weights[k] = weight;
        indices[k] = probeIndex;
        sum += weight;
    }

    if (sum > 0.0)
        weights /= sum;
}

vec3 XRENGINE_ResolveForwardAmbientFallback()
{
    float ambientMax = max(GlobalAmbient.r, max(GlobalAmbient.g, GlobalAmbient.b));
    if (ambientMax <= 0.0001)
        return vec3(XRENGINE_MIN_FORWARD_AMBIENT_FALLBACK);

    float scale = max(1.0, XRENGINE_MIN_FORWARD_AMBIENT_FALLBACK / ambientMax);
    return GlobalAmbient * scale;
}

vec3 XRENGINE_GetForwardAmbientFallback(vec3 albedo, vec3 diffuseAO)
{
    return XRENGINE_ResolveForwardAmbientFallback() * albedo * diffuseAO;
}

vec3 XRENGINE_CalculateAmbientPbr(vec3 normal, vec3 fragPos, vec3 albedo, vec3 viewDir, vec3 rms, float ambientOcclusion)
{
    float roughness = rms.x;
    float metallic = rms.y;
    float specularIntensity = rms.z;

    float NoV = max(dot(normal, viewDir), 0.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 kS = XRENGINE_SpecF_SchlickRoughnessApprox(NoV, F0, roughness) * specularIntensity;
    vec3 kD = (1.0 - kS) * (1.0 - metallic);
    vec3 diffuseAO = AmbientOcclusionMultiBounce
        ? XRENGINE_MultiBounceAO(ambientOcclusion, albedo)
        : vec3(ambientOcclusion);
    float specularOcclusion = SpecularOcclusionEnabled
        ? XRENGINE_GTSpecularOcclusion(NoV, ambientOcclusion, roughness)
        : ambientOcclusion;

    if (!ForwardPbrResourcesEnabled || ProbeCount <= 0)
        return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);

    vec4 probeWeights = vec4(0.0);
    ivec4 probeIndices = ivec4(-1);
    bool isBarycentric = false;
    if (UseProbeGrid)
        XRENGINE_ResolveProbeWeightsGrid(fragPos, probeWeights, probeIndices, isBarycentric);
#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
    else
        XRENGINE_ResolveProbeWeights(fragPos, probeWeights, probeIndices);
#endif

    vec3 reflectionDir = reflect(-viewDir, normal);
    vec3 irradianceColor = vec3(0.0);
    vec3 prefilteredColor = vec3(0.0);
    float totalWeight = 0.0;
    // Loop-invariant prefilter LOD bounds: hoisted out of the per-probe loop.
    float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
    float clampedLod = min(roughness * MAX_REFLECTION_LOD, maxMip);

    for (int i = 0; i < 4; ++i)
    {
        int probeIndex = probeIndices[i];
        if (probeIndex < 0)
            continue;

        // Barycentric tetra weights are already mathematically continuous.
        // Multiplying by per-probe influence would create hard edges at
        // influence-sphere boundaries, so we only apply influence for the
        // distance-weighted fallback path.
        float weight = isBarycentric
            ? probeWeights[i]
            : probeWeights[i] * XRENGINE_ComputeInfluenceWeight(probeIndex, fragPos);
        if (weight <= 0.0)
            continue;

        vec3 diffuseDir = XRENGINE_ApplyParallax(probeIndex, normal, fragPos);
        vec3 specDir = XRENGINE_ApplyParallax(probeIndex, reflectionDir, fragPos);
        float normalizationScale = max(ProbeParams[probeIndex].ProxyHalfExtents.w, 0.0001);
        float weightedScale = weight * normalizationScale;

        irradianceColor += weightedScale * XRENGINE_SampleOctaArray(IrradianceArray, diffuseDir, probeIndex);
        prefilteredColor += weightedScale * XRENGINE_SampleOctaArrayLod(PrefilterArray, specDir, probeIndex, clampedLod);
        totalWeight += weight;
    }

    if (totalWeight <= 0.0)
    {
        return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);
    }

    irradianceColor /= totalWeight;
    prefilteredColor /= totalWeight;

    vec2 brdfValue = texture(BRDF, vec2(NoV, roughness)).rg;
    vec3 diffuse = GlobalAmbient * irradianceColor * albedo;
    vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
    return kD * diffuse * diffuseAO + specular * specularOcclusion;
}

float XRENGINE_GetShadowBiasRange(float diffuseFactor, float biasMin, float biasMax)
{
    vec4 shadowParams = XRENGINE_GetForwardShadowParams0();
    float baseMapped = max(shadowParams.x, 0.0) * (1.0 - max(diffuseFactor, 0.0));
    float shadowMult = shadowParams.y;
    float mapped = abs(shadowMult - 1.0) < 1e-3
        ? baseMapped
        : pow(baseMapped, max(shadowMult, 0.0001));
    return mix(biasMin, biasMax, clamp(mapped, 0.0, 1.0));
}

float XRENGINE_GetShadowBias(float diffuseFactor)
{
    vec4 shadowParams = XRENGINE_GetForwardShadowParams0();
    return XRENGINE_GetShadowBiasRange(diffuseFactor, shadowParams.z, shadowParams.w);
}

float XRENGINE_SampleForwardContactShadowScreenSpace(
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    if (ForwardContactShadowsArrayEnabled)
    {
        return XRENGINE_SampleContactShadowScreenSpace(
            ForwardContactDepthViewArray,
            ForwardContactNormalViewArray,
            float(XRENGINE_GetForwardResolvedViewIndex()),
            vec2(ScreenWidth, ScreenHeight),
            XRENGINE_GetForwardResolvedViewMatrix(),
            XRENGINE_GetForwardResolvedInverseProjMatrix(),
            XRENGINE_GetForwardResolvedInverseViewMatrix(),
            XRENGINE_GetForwardResolvedViewProjectionMatrix(),
            DepthMode,
            fragPosWS,
            normalWS,
            lightDirWS,
            receiverOffset,
            compareBias,
            contactDistance,
            contactSamples,
            contactThickness,
            contactFadeStart,
            contactFadeEnd,
            contactNormalOffset,
            jitterStrength,
            viewDepth);
    }

    return XRENGINE_SampleContactShadowScreenSpace(
        ForwardContactDepthView,
        ForwardContactNormalView,
        vec2(ScreenWidth, ScreenHeight),
        XRENGINE_GetForwardResolvedViewMatrix(),
        XRENGINE_GetForwardResolvedInverseProjMatrix(),
        XRENGINE_GetForwardResolvedInverseViewMatrix(),
        XRENGINE_GetForwardResolvedViewProjectionMatrix(),
        DepthMode,
        fragPosWS,
        normalWS,
        lightDirWS,
        receiverOffset,
        compareBias,
        contactDistance,
        contactSamples,
        contactThickness,
        contactFadeStart,
        contactFadeEnd,
        contactNormalOffset,
        jitterStrength,
        viewDepth);
}

float XRENGINE_SampleDirectionalAtlasPage(
    int pageIndex,
    vec3 localCoord,
    vec4 uvScaleBias,
    float localTexelSize,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    if (pageIndex < 0 || pageIndex >= XRENGINE_GetDirectionalShadowAtlasLayerCount())
        return 1.0;

    return XRENGINE_SampleShadowAtlasFiltered(
        DirectionalShadowAtlas,
        localCoord,
        float(pageIndex),
        uvScaleBias,
        localTexelSize,
        bias,
        blockerSamples,
        filterSamples,
        filterRadius,
        blockerSearchRadius,
        softMode,
        lightSourceRadius,
        minPenumbra,
        maxPenumbra,
        vogelTapCount);
}

// Returns -1 outside the cascade so callers can fall back to lit/contact-only.
float XRENGINE_ReadCascadeShadowMapDir(int lightIndex, DirLight light, vec3 fragPos, vec3 normal, float diffuseFactor, int cascadeIndex)
{
    ivec4 shadowI0 = XRENGINE_GetForwardShadowPackedI0();
    ivec4 shadowI1 = XRENGINE_GetForwardShadowPackedI1();
    vec4 shadowF1 = XRENGINE_GetForwardShadowParams1();
    vec4 shadowF2 = XRENGINE_GetForwardShadowParams2();
    vec4 shadowF3 = XRENGINE_GetForwardShadowParams3();
    int atlasRecordIndex = lightIndex * XRENGINE_MAX_CASCADES + cascadeIndex;
    mat4 lightMatrix = light.CascadeMatrices[cascadeIndex];
    float receiverOffset = light.CascadeReceiverOffsets[cascadeIndex];

    float atlasResolutionScale = 1.0;
    bool atlasMetadataEnabled = false;
    if (DirectionalShadowAtlasEnabled[lightIndex] != 0)
    {
        ivec4 atlasState = DirectionalShadowAtlasPacked0[atlasRecordIndex];
        atlasMetadataEnabled = atlasState.x != 0;
        if (atlasMetadataEnabled)
            atlasResolutionScale = max(DirectionalShadowAtlasParams1[atlasRecordIndex].w, 1.0);
    }

    vec3 offsetPosWS = fragPos + normal * receiverOffset;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return -1.0;

    float cascadeScale = 1.0 + float(cascadeIndex) * 0.35;
    float filterRadius = shadowF1.x * cascadeScale;
    float atlasAuthoredTexelSize = DirectionalShadowAtlasParams1[atlasRecordIndex].z / atlasResolutionScale;
    vec2 shadowTexelSize = atlasMetadataEnabled && DirectionalShadowAtlasParams1[atlasRecordIndex].z > 0.0
        ? vec2(max(atlasAuthoredTexelSize, 1e-7))
        : 1.0 / vec2(textureSize(DirectionalShadowMapArrays[lightIndex], 0).xy);
    float bias = XRENGINE_ComputeShadowDepthBias(
        fragCoord,
        shadowTexelSize,
        filterRadius,
        light.CascadeBiasMin[cascadeIndex],
        light.CascadeBiasMax[cascadeIndex]);
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.z,
        viewDepth,
        shadowF2.y);
    vec3 lightDirWS = normalize(-light.Direction);
    float contact = 1.0;
    if (shadowI1.y != 0)
    {
        contact = ForwardContactShadowsEnabled
            ? XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDirWS,
                receiverOffset,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth)
            : XRENGINE_SampleContactShadowArray(
                DirectionalShadowMapArrays[lightIndex],
                lightMatrix,
                float(cascadeIndex),
                fragPos,
                normal,
                lightDirWS,
                receiverOffset,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
    }

    int encoding = DirectionalShadowMapEncoding[lightIndex];
    vec4 momentParams = DirectionalShadowMomentParams0[lightIndex];
    vec4 momentFilterParams = DirectionalShadowMomentFilterParams[lightIndex];
    bool useMomentMipmaps = momentFilterParams.z != 0.0;
    float momentMipLevel = XRENGINE_ResolveShadowMomentMipLevel(
        momentFilterParams.w,
        momentFilterParams.x,
        useMomentMipmaps);
    float momentReceiverDepth = clamp(fragCoord.z - min(bias, 0.01), 0.0, 1.0);

    if (DirectionalShadowAtlasEnabled[lightIndex] != 0)
    {
        ivec4 atlasI0 = DirectionalShadowAtlasPacked0[atlasRecordIndex];
        bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < XRENGINE_GetDirectionalShadowAtlasLayerCount();
        int fallbackMode = atlasI0.z;
        if (atlasEnabled)
        {
            vec4 atlasUvScaleBias = DirectionalShadowAtlasParams0[atlasRecordIndex];
            float atlasLocalTexelSize = max(atlasAuthoredTexelSize, 1e-7);
            float atlasBias = XRENGINE_ComputeShadowDepthBias(
                fragCoord,
                vec2(atlasLocalTexelSize),
                filterRadius,
                light.CascadeBiasMin[cascadeIndex],
                light.CascadeBiasMax[cascadeIndex]);
            if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
            {
                vec2 atlasUv = XRENGINE_ShadowAtlasUvFromLocal(fragCoord.xy, atlasUvScaleBias);
                float atlasMomentReceiverDepth = clamp(fragCoord.z - min(atlasBias, 0.01), 0.0, 1.0);
                return XRENGINE_SampleShadowMoment2DArray(
                    DirectionalShadowAtlas,
                    atlasUv,
                    float(atlasI0.y),
                    atlasMomentReceiverDepth,
                    encoding,
                    momentParams.x,
                    momentParams.y,
                    momentParams.z,
                    momentParams.w,
                    0.0,
                    false) * contact;
            }

            return XRENGINE_SampleDirectionalAtlasPage(
                atlasI0.y,
                fragCoord,
                atlasUvScaleBias,
                atlasLocalTexelSize,
                atlasBias,
                shadowI0.x,
                shadowI0.y,
                filterRadius,
                shadowF1.y * cascadeScale,
                shadowI0.w,
                shadowF2.x,
                shadowF1.z * cascadeScale,
                shadowF1.w * cascadeScale,
                shadowI0.z) * contact;
        }

        if (fallbackMode == 2)
            return contact;
        if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
        {
            return XRENGINE_SampleShadowMoment2DArray(
                DirectionalShadowMapArrays[lightIndex],
                fragCoord.xy,
                float(cascadeIndex),
                momentReceiverDepth,
                encoding,
                momentParams.x,
                momentParams.y,
                momentParams.z,
                momentParams.w,
                momentMipLevel,
                useMomentMipmaps) * contact;
        }

        return XRENGINE_SampleShadowMapArrayFiltered(
            DirectionalShadowMapArrays[lightIndex],
            fragCoord,
            float(cascadeIndex),
            bias,
            shadowI0.x,
            shadowI0.y,
            filterRadius,
            shadowF1.y * cascadeScale,
            shadowI0.w,
            shadowF2.x,
            shadowF1.z * cascadeScale,
            shadowF1.w * cascadeScale,
            shadowI0.z) * contact;
    }

    if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        return XRENGINE_SampleShadowMoment2DArray(
            DirectionalShadowMapArrays[lightIndex],
            fragCoord.xy,
            float(cascadeIndex),
            momentReceiverDepth,
            encoding,
            momentParams.x,
            momentParams.y,
            momentParams.z,
            momentParams.w,
            momentMipLevel,
            useMomentMipmaps) * contact;
    }

    return XRENGINE_SampleShadowMapArrayFiltered(
        DirectionalShadowMapArrays[lightIndex],
        fragCoord,
        float(cascadeIndex),
        bias,
        shadowI0.x,
        shadowI0.y,
        filterRadius,
        shadowF1.y * cascadeScale,
        shadowI0.w,
        shadowF2.x,
        shadowF1.z * cascadeScale,
        shadowF1.w * cascadeScale,
        shadowI0.z) * contact;
}

float XRENGINE_ReadDirectionalContactShadowOnly(int lightIndex, DirLight light, vec3 fragPos, vec3 normal, float diffuseFactor)
{
    ivec4 shadowI1 = XRENGINE_GetForwardShadowPackedI1();
    vec4 shadowF2 = XRENGINE_GetForwardShadowParams2();
    vec4 shadowF3 = XRENGINE_GetForwardShadowParams3();

    if (shadowI1.y == 0 ||
        !ForwardContactShadowsEnabled ||
        lightIndex < 0 ||
        lightIndex >= XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS)
    {
        return 1.0;
    }

    vec4 biasProjectionParams = DirectionalShadowBiasProjectionParams[lightIndex];
    float bias = max(biasProjectionParams.x, 0.0);
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.z,
        viewDepth,
        shadowF2.y);

    return XRENGINE_SampleForwardContactShadowScreenSpace(
        fragPos,
        normal,
        normalize(-light.Direction),
        max(biasProjectionParams.y, 0.0),
        bias,
        shadowF2.y,
        contactSampleCount,
        shadowF2.z,
        shadowF2.w,
        shadowF3.x,
        shadowF3.y,
        shadowF3.z,
        viewDepth);
}

// Shadow map reading for a forward directional light.
float XRENGINE_ReadShadowMapDir(int lightIndex, DirLight light, vec3 fragPos, vec3 normal, float diffuseFactor)
{
    ivec4 shadowI0 = XRENGINE_GetForwardShadowPackedI0();
    ivec4 shadowI1 = XRENGINE_GetForwardShadowPackedI1();
    vec4 shadowF1 = XRENGINE_GetForwardShadowParams1();
    vec4 shadowF2 = XRENGINE_GetForwardShadowParams2();
    vec4 shadowF3 = XRENGINE_GetForwardShadowParams3();

    if (lightIndex < 0 || lightIndex >= XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS)
        return 1.0;

    if (DirectionalShadowMapEnabled[lightIndex] == 0)
        return XRENGINE_ReadDirectionalContactShadowOnly(lightIndex, light, fragPos, normal, diffuseFactor);

    if (DirectionalUseCascadedShadows[lightIndex] != 0 && shadowI1.x != 0 && light.CascadeCount > 0)
    {
        float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
        int cascadeCount = min(light.CascadeCount, XRENGINE_MAX_CASCADES);

        for (int i = 0; i < cascadeCount; ++i)
        {
            float splitFar = light.CascadeSplits[i];
            bool isLast = (i == cascadeCount - 1);

            if (viewDepth <= splitFar)
            {
                float shadow0 = XRENGINE_ReadCascadeShadowMapDir(lightIndex, light, fragPos, normal, diffuseFactor, i);
                if (shadow0 < 0.0) shadow0 = 1.0;

                float blendWidth = light.CascadeBlendWidths[i];
                if (blendWidth > 0.0 && viewDepth > splitFar - blendWidth)
                {
                    float t = clamp((viewDepth - (splitFar - blendWidth)) / blendWidth, 0.0, 1.0);
                    if (!isLast)
                    {
                        float shadow1 = XRENGINE_ReadCascadeShadowMapDir(lightIndex, light, fragPos, normal, diffuseFactor, i + 1);
                        if (shadow1 < 0.0) shadow1 = shadow0;
                        return mix(shadow0, shadow1, t);
                    }

                    float shadow1 = XRENGINE_ReadDirectionalContactShadowOnly(lightIndex, light, fragPos, normal, diffuseFactor);
                    return mix(shadow0, shadow1, t);
                }

                return shadow0;
            }
        }

        return XRENGINE_ReadDirectionalContactShadowOnly(lightIndex, light, fragPos, normal, diffuseFactor);
    }

    // Fallback: standalone (non-cascade) shadow map.
    int atlasRecordIndex = lightIndex * XRENGINE_MAX_CASCADES;
    mat4 lightMatrix = light.WorldToLightSpaceMatrix;
    vec4 biasProjectionParams = DirectionalShadowBiasProjectionParams[lightIndex];
    float receiverOffset = max(biasProjectionParams.y, 0.0);
    float atlasResolutionScale = 1.0;
    bool atlasMetadataEnabled = false;
    if (DirectionalShadowAtlasEnabled[lightIndex] != 0)
    {
        ivec4 atlasState = DirectionalShadowAtlasPacked0[atlasRecordIndex];
        atlasMetadataEnabled = atlasState.x != 0;
        if (atlasMetadataEnabled)
            atlasResolutionScale = max(DirectionalShadowAtlasParams1[atlasRecordIndex].w, 1.0);
    }
    vec3 offsetPosWS = fragPos + normal * receiverOffset;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

    // Outside shadow map bounds: treat as fully lit/contact-only.
    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return XRENGINE_ReadDirectionalContactShadowOnly(lightIndex, light, fragPos, normal, diffuseFactor);

    float atlasAuthoredTexelSize = DirectionalShadowAtlasParams1[atlasRecordIndex].z / atlasResolutionScale;
    vec2 shadowTexelSize = atlasMetadataEnabled && DirectionalShadowAtlasParams1[atlasRecordIndex].z > 0.0
        ? vec2(max(atlasAuthoredTexelSize, 1e-7))
        : 1.0 / vec2(textureSize(DirectionalShadowMaps[lightIndex], 0));
    float bias = XRENGINE_ComputeShadowDepthBias(
        fragCoord,
        shadowTexelSize,
        shadowF1.x,
        biasProjectionParams.x,
        ShadowBiasParams.y);
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.z,
        viewDepth,
        shadowF2.y);
    vec3 lightDirWS = normalize(-light.Direction);
    float contact = 1.0;
    if (shadowI1.y != 0)
    {
        contact = ForwardContactShadowsEnabled
            ? XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDirWS,
                receiverOffset,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth)
            : XRENGINE_SampleContactShadow2D(
                DirectionalShadowMaps[lightIndex],
                lightMatrix,
                fragPos,
                normal,
                lightDirWS,
                receiverOffset,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
    }

    if (DirectionalShadowAtlasEnabled[lightIndex] != 0)
    {
        ivec4 atlasI0 = DirectionalShadowAtlasPacked0[atlasRecordIndex];
        bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < XRENGINE_GetDirectionalShadowAtlasLayerCount();
        int fallbackMode = atlasI0.z;
        if (atlasEnabled)
        {
            vec4 atlasUvScaleBias = DirectionalShadowAtlasParams0[atlasRecordIndex];
            float atlasLocalTexelSize = max(atlasAuthoredTexelSize, 1e-7);
            float atlasBias = XRENGINE_ComputeShadowDepthBias(
                fragCoord,
                vec2(atlasLocalTexelSize),
                shadowF1.x,
                biasProjectionParams.x,
                ShadowBiasParams.y);
            return XRENGINE_SampleDirectionalAtlasPage(
                atlasI0.y,
                fragCoord,
                atlasUvScaleBias,
                atlasLocalTexelSize,
                atlasBias,
                shadowI0.x,
                shadowI0.y,
                shadowF1.x,
                shadowF1.y,
                shadowI0.w,
                shadowF2.x,
                shadowF1.z,
                shadowF1.w,
                shadowI0.z) * contact;
        }

        if (fallbackMode == 2)
            return contact;
        return XRENGINE_SampleShadowMapFiltered(
            DirectionalShadowMaps[lightIndex],
            fragCoord,
            bias,
            shadowI0.x,
            shadowI0.y,
            shadowF1.x,
            shadowF1.y,
            shadowI0.w,
            shadowF2.x,
            shadowF1.z,
            shadowF1.w,
            shadowI0.z) * contact;
    }

    int encoding = DirectionalShadowMapEncoding[lightIndex];
    if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        vec4 momentParams = DirectionalShadowMomentParams0[lightIndex];
        vec4 momentFilter = DirectionalShadowMomentFilterParams[lightIndex];
        bool useMomentMipmaps = momentFilter.z != 0.0;
        float momentMipLevel = XRENGINE_ResolveShadowMomentMipLevel(
            momentFilter.w,
            momentFilter.x,
            useMomentMipmaps);
        return XRENGINE_SampleShadowMoment2D(
            DirectionalShadowMaps[lightIndex],
            fragCoord.xy,
            clamp(fragCoord.z - min(bias, 0.01), 0.0, 1.0),
            encoding,
            momentParams.x,
            momentParams.y,
            momentParams.z,
            momentParams.w,
            momentMipLevel,
            useMomentMipmaps) * contact;
    }

    return XRENGINE_SampleShadowMapFiltered(
        DirectionalShadowMaps[lightIndex],
        fragCoord,
        bias,
        shadowI0.x,
        shadowI0.y,
        shadowF1.x,
        shadowF1.y,
        shadowI0.w,
        shadowF2.x,
        shadowF1.z,
        shadowF1.w,
        shadowI0.z) * contact;
}

vec3 XRENGINE_CalculateDirectPbrLightWithViewDir(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation, vec3 viewDir)
{
    vec3 halfVector = normalize(viewDir + lightDirection);
    float NoL = max(dot(normal, lightDirection), 0.0);
    if (NoL <= 0.0)
        return vec3(0.0);

    float NoH = max(dot(normal, halfVector), 0.0);
    float NoV = max(dot(normal, viewDir), 0.0);
    float HoV = max(dot(halfVector, viewDir), 0.0);
    float roughness = rms.x;
    float metallic = rms.y;
    float specularIntensity = rms.z;
    float a = roughness * roughness;
    float k = (roughness + 1.0);
    k = k * k * 0.125;

    float D = XRENGINE_SpecD_TRGGX(NoH * NoH, a * a);
    float G = XRENGINE_SpecG_Smith(NoV, NoL, k);
    vec3 F = XRENGINE_SpecF_SchlickApprox(HoV, F0);
    vec3 specular = specularIntensity * D * G * F / (4.0 * NoV * NoL + 0.0001);
    vec3 kD = (1.0 - F) * (1.0 - metallic);
    vec3 radiance = attenuation * lightColor * diffuseIntensity;
    return (kD * albedo / PI + specular) * radiance * NoL;
}

vec3 XRENGINE_CalculateDirectPbrLight(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalculateDirectPbrLightWithViewDir(
        lightColor,
        diffuseIntensity,
        lightDirection,
        normal,
        fragPos,
        albedo,
        rms,
        F0,
        attenuation,
        viewDir);
}

vec3 XRENGINE_CalcDirLightWithViewDir(int lightIndex, DirLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, bool useShadow, vec3 viewDir)
{
    vec3 lightDir = normalize(-light.Direction);
    float shadow = useShadow ? XRENGINE_ReadShadowMapDir(lightIndex, light, fragPos, normal, max(dot(normal, lightDir), 0.0)) : 1.0;
    return XRENGINE_CalculateDirectPbrLightWithViewDir(light.Base.Color, light.Base.DiffuseIntensity, lightDir, normal, fragPos, albedo, rms, F0, 1.0, viewDir) * shadow;
}

vec3 XRENGINE_CalcDirLight(int lightIndex, DirLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, bool useShadow)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalcDirLightWithViewDir(lightIndex, light, normal, fragPos, albedo, rms, F0, useShadow, viewDir);
}

// Forward+ light lists vary per fragment. OpenGL sampler arrays require
// dynamically uniform indices, so point shadow cubemaps dispatch through
// fixed-slot branches instead of indexing the sampler array by runtime slot.
float XRENGINE_GetPointShadowSampleRadiusForSlot(int shadowSlot, float filterRadius)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[0], filterRadius);
    case 1:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[1], filterRadius);
    case 2:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[2], filterRadius);
    case 3:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[3], filterRadius);
    default:
        return filterRadius;
    }
}

float XRENGINE_GetPointShadowFaceSizeForSlot(int shadowSlot)
{
    switch (shadowSlot)
    {
    case 0:
        return max(float(textureSize(PointLightShadowMaps[0], 0).x), 1.0);
    case 1:
        return max(float(textureSize(PointLightShadowMaps[1], 0).x), 1.0);
    case 2:
        return max(float(textureSize(PointLightShadowMaps[2], 0).x), 1.0);
    case 3:
        return max(float(textureSize(PointLightShadowMaps[3], 0).x), 1.0);
    default:
        return 1.0;
    }
}

float XRENGINE_GetPointShadowTexelRelativeBiasForSlot(int shadowSlot, float NoL, vec4 biasParams, float filterRadius)
{
    float faceSize = XRENGINE_GetPointShadowFaceSizeForSlot(shadowSlot);
    float texelRel = 2.0 / faceSize;
    float NoLSafe = max(NoL, 0.05);
    float tanTheta = sqrt(max(1.0 - NoLSafe * NoLSafe, 0.0)) / NoLSafe;
    float filterTexels = max(1.0, clamp(filterRadius * 256.0, 0.0, 8.0));
    float depthTexels = XRENGINE_GetBiasParamDepthTexels(biasParams);
    float slopeTexels = XRENGINE_GetBiasParamSlopeTexels(biasParams);
    return texelRel * (depthTexels + slopeTexels * tanTheta * filterTexels);
}

float XRENGINE_SamplePointContactShadowCubeSlot(
    int shadowSlot,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightPosWS,
    float receiverOffset,
    float compareBias,
    float farPlaneDist,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[0], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 1:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[1], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 2:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[2], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 3:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[3], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    default:
        return 1.0;
    }
}

float XRENGINE_SamplePointShadowCubeSlot(
    int shadowSlot,
    vec3 shadowDir,
    float compareDepth,
    float bias,
    float farPlaneDist,
    float sampleRadius,
    float blockerSearchRadius,
    int blockerSamples,
    int filterSamples,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[0], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 1:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[1], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 2:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[2], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 3:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[3], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    default:
        return 1.0;
    }
}

float XRENGINE_ReadPointShadowCenterDepthForSlot(int shadowSlot, vec3 shadowDir, float farPlaneDist)
{
    switch (shadowSlot)
    {
    case 0:
        return texture(PointLightShadowMaps[0], shadowDir).r * farPlaneDist;
    case 1:
        return texture(PointLightShadowMaps[1], shadowDir).r * farPlaneDist;
    case 2:
        return texture(PointLightShadowMaps[2], shadowDir).r * farPlaneDist;
    case 3:
        return texture(PointLightShadowMaps[3], shadowDir).r * farPlaneDist;
    default:
        return farPlaneDist;
    }
}

int XRENGINE_SelectPointShadowAtlasFace(vec3 lightToReceiver, out vec2 localUv)
{
    vec3 a = abs(lightToReceiver);
    if (a.x >= a.y && a.x >= a.z)
    {
        if (lightToReceiver.x >= 0.0)
        {
            localUv = vec2(-lightToReceiver.z, -lightToReceiver.y) / max(a.x, 1e-6);
            localUv = localUv * 0.5 + vec2(0.5);
            return 0;
        }

        localUv = vec2(lightToReceiver.z, -lightToReceiver.y) / max(a.x, 1e-6);
        localUv = localUv * 0.5 + vec2(0.5);
        return 1;
    }

    if (a.y >= a.z)
    {
        if (lightToReceiver.y >= 0.0)
        {
            localUv = vec2(lightToReceiver.x, lightToReceiver.z) / max(a.y, 1e-6);
            localUv = localUv * 0.5 + vec2(0.5);
            return 2;
        }

        localUv = vec2(lightToReceiver.x, -lightToReceiver.z) / max(a.y, 1e-6);
        localUv = localUv * 0.5 + vec2(0.5);
        return 3;
    }

    if (lightToReceiver.z >= 0.0)
    {
        localUv = vec2(lightToReceiver.x, -lightToReceiver.y) / max(a.z, 1e-6);
        localUv = localUv * 0.5 + vec2(0.5);
        return 4;
    }

    localUv = vec2(-lightToReceiver.x, -lightToReceiver.y) / max(a.z, 1e-6);
    localUv = localUv * 0.5 + vec2(0.5);
    return 5;
}

float XRENGINE_SamplePointAtlasPage(
    int pageIndex,
    vec3 localCoord,
    vec4 uvScaleBias,
    float localTexelSize,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    if (pageIndex < 0 || pageIndex >= XRENGINE_GetPointShadowAtlasLayerCount())
        return 1.0;

    return XRENGINE_SampleShadowAtlasFiltered(
        PointLightShadowAtlas,
        localCoord,
        float(pageIndex),
        uvScaleBias,
        localTexelSize,
        bias,
        blockerSamples,
        filterSamples,
        filterRadius,
        blockerSearchRadius,
        softMode,
        lightSourceRadius,
        minPenumbra,
        maxPenumbra,
        vogelTapCount);
}

float XRENGINE_ReadPointAtlasCenterDepth(int pageIndex, vec2 localUv, vec4 uvScaleBias)
{
    if (pageIndex < 0 || pageIndex >= XRENGINE_GetPointShadowAtlasLayerCount())
        return 1.0;

    return XRENGINE_ReadShadowAtlasDepth(PointLightShadowAtlas, localUv, float(pageIndex), uvScaleBias);
}

float XRENGINE_GetPointAtlasSampleRadius(float authoredTexelSize, float filterRadius)
{
    float requestedScale = clamp(filterRadius * 256.0, 0.0, 4.0);
    return 2.0 * max(authoredTexelSize, 1e-7) * max(1.0, requestedScale);
}

float XRENGINE_ReadPointAtlasDepthForDirection(
    ForwardPointShadowData shadowData,
    vec3 sampleDirection,
    out bool sampleable)
{
    vec2 localUv;
    int faceIndex = XRENGINE_SelectPointShadowAtlasFace(sampleDirection, localUv);
    if (faceIndex < 0 || faceIndex >= XRENGINE_POINT_SHADOW_FACE_COUNT)
    {
        sampleable = false;
        return 1.0;
    }

    ivec4 atlasI0 = shadowData.AtlasPacked0[faceIndex];
    sampleable = atlasI0.x != 0 &&
        atlasI0.y >= 0 &&
        atlasI0.y < XRENGINE_GetPointShadowAtlasLayerCount();
    if (!sampleable)
        return 1.0;

    return XRENGINE_ReadShadowAtlasDepth(
        PointLightShadowAtlas,
        localUv,
        float(atlasI0.y),
        shadowData.AtlasParams0[faceIndex]);
}

float XRENGINE_SamplePointAtlasDirection(
    ForwardPointShadowData shadowData,
    vec3 sampleDirection,
    float receiverDepth,
    float bias)
{
    bool sampleable;
    float sampleDepth = XRENGINE_ReadPointAtlasDepthForDirection(shadowData, sampleDirection, sampleable);
    if (!sampleable)
        return 1.0;

    return (receiverDepth - bias) <= sampleDepth ? 1.0 : 0.0;
}

float XRENGINE_SamplePointAtlasCubeSimple(
    ForwardPointShadowData shadowData,
    vec3 shadowDir,
    float receiverDepth,
    float bias)
{
    return XRENGINE_SamplePointAtlasDirection(shadowData, normalize(shadowDir), receiverDepth, bias);
}

float XRENGINE_SamplePointAtlasCubePCF(
    ForwardPointShadowData shadowData,
    vec3 shadowDir,
    float receiverDepth,
    float bias,
    float sampleRadius)
{
    float lit = 0.0;
    vec3 baseDir = normalize(shadowDir);
    float radius = max(sampleRadius, 0.000001);

    for (int i = 0; i < 20; ++i)
    {
        vec3 sampleDir = normalize(baseDir + XRENGINE_GetShadowCubeKernelTap(i) * radius);
        lit += XRENGINE_SamplePointAtlasDirection(shadowData, sampleDir, receiverDepth, bias);
    }

    return lit / 20.0;
}

float XRENGINE_SamplePointAtlasCubeVogel(
    ForwardPointShadowData shadowData,
    vec3 shadowDir,
    float receiverDepth,
    float bias,
    float sampleRadius,
    int tapCount)
{
    int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedTaps <= 1)
        return XRENGINE_SamplePointAtlasCubeSimple(shadowData, shadowDir, receiverDepth, bias);

    vec3 baseDir = normalize(shadowDir);
    vec3 tangent;
    vec3 bitangent;
    XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent);
    float radius = max(sampleRadius, 0.000001);
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;
    float lit = 0.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedTaps)
            break;

        vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * radius;
        vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y);
        lit += XRENGINE_SamplePointAtlasDirection(shadowData, sampleDir, receiverDepth, bias);
    }

    return lit / float(clampedTaps);
}

float XRENGINE_BlockerSearchPointAtlasCube(
    ForwardPointShadowData shadowData,
    vec3 shadowDir,
    float receiverDepth,
    float sampleRadius,
    int sampleCount)
{
    float blockerSum = 0.0;
    int blockerCount = 0;
    int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps);
    vec3 baseDir = normalize(shadowDir);
    vec3 tangent;
    vec3 bitangent;
    XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent);
    float radius = max(sampleRadius, 0.000001);
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius;
        vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y);
        bool sampleable;
        float sampleDepth = XRENGINE_ReadPointAtlasDepthForDirection(shadowData, sampleDir, sampleable);
        if (sampleable && sampleDepth < receiverDepth)
        {
            blockerSum += sampleDepth;
            blockerCount++;
        }
    }

    return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;
}

float XRENGINE_SamplePointAtlasCubeCHSS(
    ForwardPointShadowData shadowData,
    vec3 shadowDir,
    float receiverDepth,
    float bias,
    float farPlaneDist,
    float blockerSearchRadius,
    int blockerSamples,
    int filterSamples,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra)
{
    float biasedReceiverDepth = receiverDepth - bias;
    float avgBlockerDepth = XRENGINE_BlockerSearchPointAtlasCube(
        shadowData,
        shadowDir,
        biasedReceiverDepth,
        blockerSearchRadius,
        blockerSamples);

    if (avgBlockerDepth < 0.0)
        return 1.0;

    float receiverWorldDepth = biasedReceiverDepth * farPlaneDist;
    float blockerWorldDepth = avgBlockerDepth * farPlaneDist;
    float angularSourceRadius = max(lightSourceRadius, 0.0) / max(blockerWorldDepth, 0.0001);
    float rawPenumbra = (receiverWorldDepth - blockerWorldDepth) / max(blockerWorldDepth, 0.0001) * angularSourceRadius;
    float penumbra = XRENGINE_ClampPenumbra(rawPenumbra, max(minPenumbra, 0.000001), max(maxPenumbra, minPenumbra));

    return XRENGINE_SamplePointAtlasCubeVogel(shadowData, shadowDir, receiverDepth, bias, penumbra, filterSamples);
}

float XRENGINE_SamplePointAtlasCubeFiltered(
    ForwardPointShadowData shadowData,
    vec3 shadowDir,
    float receiverDepth,
    float bias,
    float farPlaneDist,
    float sampleRadius,
    float blockerSearchRadius,
    int blockerSamples,
    int filterSamples,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    if (softShadowMode == 3)
        return XRENGINE_SamplePointAtlasCubeVogel(shadowData, shadowDir, receiverDepth, bias, sampleRadius, vogelTapCount);

    int clampedFilterSamples = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedFilterSamples <= 1)
        return XRENGINE_SamplePointAtlasCubeSimple(shadowData, shadowDir, receiverDepth, bias);

    if (softShadowMode == 2)
        return XRENGINE_SamplePointAtlasCubeCHSS(
            shadowData,
            shadowDir,
            receiverDepth,
            bias,
            farPlaneDist,
            blockerSearchRadius,
            blockerSamples,
            clampedFilterSamples,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra);

    if (softShadowMode == 1 || clampedFilterSamples <= 4)
        return XRENGINE_SamplePointAtlasCubeVogel(shadowData, shadowDir, receiverDepth, bias, sampleRadius, clampedFilterSamples);

    return XRENGINE_SamplePointAtlasCubePCF(shadowData, shadowDir, receiverDepth, bias, sampleRadius);
}

float XRENGINE_SampleSpotContactShadow2DSlot(
    int shadowSlot,
    mat4 worldToLightSpaceProjMatrix,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[0], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 1:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[1], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 2:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[2], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 3:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[3], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    default:
        return 1.0;
    }
}

float XRENGINE_SampleSpotShadowMoment2DSlot(
    int shadowSlot,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[0], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    case 1:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[1], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    case 2:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[2], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    case 3:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[3], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    default:
        return 1.0;
    }
}

float XRENGINE_SampleSpotShadowDepth2DSlot(
    int shadowSlot,
    vec3 shadowCoord,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[0], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 1:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[1], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 2:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[2], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 3:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[3], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    default:
        return 1.0;
    }
}

float XRENGINE_EstimateSpotShadowMomentMargin2DSlot(
    int shadowSlot,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[0], uv, receiverDepth, encoding, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    case 1:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[1], uv, receiverDepth, encoding, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    case 2:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[2], uv, receiverDepth, encoding, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    case 3:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[3], uv, receiverDepth, encoding, positiveExponent, negativeExponent, mipLevel, useMipmaps);
    default:
        return 1.0;
    }
}

float XRENGINE_ReadSpotShadowCenterDepthForSlot(int shadowSlot, vec2 uv)
{
    switch (shadowSlot)
    {
    case 0:
        return texture(SpotLightShadowMaps[0], uv).r;
    case 1:
        return texture(SpotLightShadowMaps[1], uv).r;
    case 2:
        return texture(SpotLightShadowMaps[2], uv).r;
    case 3:
        return texture(SpotLightShadowMaps[3], uv).r;
    default:
        return 1.0;
    }
}

float XRENGINE_GetSpotShadowTexelSizeForSlot(int shadowSlot)
{
    switch (shadowSlot)
    {
    case 0:
        return 1.0 / max(float(textureSize(SpotLightShadowMaps[0], 0).x), 1.0);
    case 1:
        return 1.0 / max(float(textureSize(SpotLightShadowMaps[1], 0).x), 1.0);
    case 2:
        return 1.0 / max(float(textureSize(SpotLightShadowMaps[2], 0).x), 1.0);
    case 3:
        return 1.0 / max(float(textureSize(SpotLightShadowMaps[3], 0).x), 1.0);
    default:
        return 1.0 / 512.0;
    }
}

float XRENGINE_GetSpotDistanceAlongAxis(SpotLight light, vec3 fragPos)
{
    return max(dot(fragPos - light.Base.Position, normalize(light.Direction)), 0.0001);
}

float XRENGINE_GetSpotShadowWorldTexelSize(SpotLight light, vec3 fragPos, float texelUvSize)
{
    float cosOuter = clamp(light.OuterCutoff, 0.001, 0.999999);
    float tanOuter = sqrt(max(1.0 - cosOuter * cosOuter, 0.0)) / cosOuter;
    return 2.0 * XRENGINE_GetSpotDistanceAlongAxis(light, fragPos) * tanOuter * max(texelUvSize, 1e-7);
}

float XRENGINE_ReadPointContactShadowOnly(int lightIndex, PointLight light, vec3 normal, vec3 fragPos)
{
    if (!ForwardContactShadowsEnabled || lightIndex < 0 || lightIndex >= PointLightCount || lightIndex >= PointLightShadows.length())
        return 1.0;

    ForwardPointShadowData shadowData = PointLightShadows[lightIndex];
    ivec4 shadowI1 = shadowData.Packed1;
    if (shadowI1.z == 0)
        return 1.0;

    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    vec4 shadowF4 = shadowData.Params4;
    vec4 shadowF5 = shadowData.Params5;
    ivec4 shadowI2 = shadowData.Packed2;
    vec4 shadowF6 = shadowData.Params6;
    float contactDistance = shadowF2.w;
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        contactDistance);

    return XRENGINE_SampleForwardContactShadowScreenSpace(
        fragPos,
        normal,
        normalize(light.Position - fragPos),
        shadowF3.w,
        contactDistance * 0.001,
        contactDistance,
        contactSampleCount,
        shadowF3.x,
        shadowF3.y,
        shadowF3.z,
        shadowF3.w,
        shadowF4.x,
        viewDepth);
}

float XRENGINE_ReadPointAtlasShadowMap(ForwardPointShadowData shadowData, PointLight light, vec3 normal, vec3 fragPos)
{
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;
    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    vec4 shadowF4 = shadowData.Params4;
    vec4 shadowF5 = shadowData.Params5;
    ivec4 shadowI2 = shadowData.Packed2;
    vec4 shadowF6 = shadowData.Params6;

    int debugMode = shadowI1.y;
    vec3 lightToFragBase = fragPos - light.Position;
    float receiverDist = length(lightToFragBase);
    // Reuse the radial vector instead of recomputing normalize(light.Position - fragPos).
    vec3 lightDirN = receiverDist > 1e-6 ? -lightToFragBase / receiverDist : vec3(0.0, 0.0, 1.0);
    vec2 faceUv;
    int faceIndex = XRENGINE_SelectPointShadowAtlasFace(lightToFragBase, faceUv);
    ivec4 atlasI0 = shadowData.AtlasPacked0[faceIndex];
    vec4 atlasUvScaleBias = shadowData.AtlasParams0[faceIndex];
    vec4 atlasDepthParams = shadowData.AtlasParams1[faceIndex];

    float nearPlaneDist = max(atlasDepthParams.x, 0.0);
    float farPlaneDist = max(atlasDepthParams.y, nearPlaneDist + 0.001);
    if (receiverDist >= farPlaneDist)
        return 1.0;

    float localTexelSize = max(atlasDepthParams.z, 1e-7);
    float atlasResolutionScale = max(atlasDepthParams.w, 1.0);
    float authoredTexelSize = max(localTexelSize / atlasResolutionScale, 1e-7);
    float normalOffset = receiverDist * 2.0 * authoredTexelSize * XRENGINE_GetBiasParamNormalTexels(shadowF5);
    vec3 offsetPosWS = fragPos + normal * normalOffset;
    vec3 fragToLight = offsetPosWS - light.Position;
    float lightDist = length(fragToLight);
    float NoL = max(dot(normal, lightDirN), 0.0);

    if (lightDist >= farPlaneDist)
        return 1.0;

    if (lightDist <= nearPlaneDist + normalOffset)
    {
        if (debugMode != 0)
            XRENGINE_TrySetForwardShadowDebug(debugMode, 1.0, nearPlaneDist / max(farPlaneDist, 0.001));
        return 1.0;
    }

    float NoLSafe = max(NoL, 0.05);
    float tanTheta = sqrt(max(1.0 - NoLSafe * NoLSafe, 0.0)) / NoLSafe;
    float filterTexels = max(1.0, clamp(shadowF1.z * 256.0, 0.0, 8.0));
    float texelRel = 2.0 * authoredTexelSize * (
        XRENGINE_GetBiasParamDepthTexels(shadowF5) +
        XRENGINE_GetBiasParamSlopeTexels(shadowF5) * tanTheta * filterTexels);
    float r16fRel = 1.0 / 512.0;
    float relThreshold = max(texelRel, r16fRel);
    float compareBias = lightDist * relThreshold;
    float normalizedBias = compareBias / max(farPlaneDist, 0.001);
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        shadowF2.w);
    float contact = 1.0;
    if (shadowI1.z != 0 && ForwardContactShadowsEnabled)
    {
        contact = XRENGINE_SampleForwardContactShadowScreenSpace(
            fragPos,
            normal,
            lightDirN,
            normalOffset,
            compareBias,
            shadowF2.w,
            contactSampleCount,
            shadowF3.x,
            shadowF3.y,
            shadowF3.z,
            shadowF3.w,
            shadowF4.x,
            viewDepth);
    }

    if (atlasI0.x == 0)
    {
        float fallbackLit = atlasI0.z == XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY ? contact : 1.0;
        if (debugMode != 0)
            XRENGINE_TrySetForwardShadowDebug(debugMode, fallbackLit, 1.0);
        return fallbackLit;
    }

    vec2 sampledFaceUv;
    XRENGINE_SelectPointShadowAtlasFace(fragToLight, sampledFaceUv);
    float receiverDepth = clamp(lightDist / max(farPlaneDist, 0.001), 0.0, 1.0);
    float sampleRadius = XRENGINE_GetPointAtlasSampleRadius(authoredTexelSize, shadowF1.z);
    float blockerSearchRadius = XRENGINE_GetPointAtlasSampleRadius(authoredTexelSize, shadowF1.w);
    float minPenumbra = XRENGINE_GetPointAtlasSampleRadius(authoredTexelSize, shadowF2.x);
    float maxPenumbra = XRENGINE_GetPointAtlasSampleRadius(authoredTexelSize, shadowF2.y);
    int encoding = shadowI2.x;
    if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        int sampleFaceIndex = XRENGINE_SelectPointShadowAtlasFace(fragToLight, sampledFaceUv);
        ivec4 sampleAtlasI0 = shadowData.AtlasPacked0[sampleFaceIndex];
        if (sampleAtlasI0.x == 0)
        {
            float fallbackLit = sampleAtlasI0.z == XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY ? contact : 1.0;
            if (debugMode != 0)
                XRENGINE_TrySetForwardShadowDebug(debugMode, fallbackLit, 1.0);
            return fallbackLit;
        }

        vec2 atlasUv = XRENGINE_ShadowAtlasUvFromLocal(sampledFaceUv, shadowData.AtlasParams0[sampleFaceIndex]);
        float momentReceiverDepth = clamp(receiverDepth - min(normalizedBias, 0.01), 0.0, 1.0);
        float litMoment = XRENGINE_SampleShadowMoment2DArray(
            PointLightShadowAtlas,
            atlasUv,
            float(sampleAtlasI0.y),
            momentReceiverDepth,
            encoding,
            shadowF6.x,
            shadowF6.y,
            shadowF6.z,
            shadowF6.w,
            0.0,
            false) * contact;

        if (debugMode != 0)
        {
            float margin = XRENGINE_EstimateShadowMomentMargin(
                PointLightShadowAtlas,
                atlasUv,
                float(sampleAtlasI0.y),
                momentReceiverDepth,
                encoding,
                shadowF6.z,
                shadowF6.w,
                0.0,
                false);
            XRENGINE_TrySetForwardShadowDebug(debugMode, litMoment, margin);
        }

        return litMoment;
    }

    float lit = XRENGINE_SamplePointAtlasCubeFiltered(
        shadowData,
        fragToLight,
        receiverDepth,
        normalizedBias,
        farPlaneDist,
        sampleRadius,
        blockerSearchRadius,
        shadowI0.z,
        shadowI0.y,
        shadowI1.x,
        shadowF2.z,
        minPenumbra,
        maxPenumbra,
        shadowI0.w) * contact;

    if (debugMode != 0)
    {
        float centerDepth = XRENGINE_ReadPointAtlasCenterDepth(atlasI0.y, sampledFaceUv, atlasUvScaleBias);
        float margin = centerDepth - (receiverDepth - normalizedBias);
        XRENGINE_TrySetForwardShadowDebug(debugMode, lit, margin);
    }

    return lit;
}

float XRENGINE_ReadShadowMapPointLegacySampler(
    samplerCube shadowMap,
    ForwardPointShadowData shadowData,
    PointLight light,
    vec3 normal,
    vec3 fragPos)
{
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;
    vec4 shadowF0 = shadowData.Params0;
    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    vec4 shadowF4 = shadowData.Params4;
    vec4 shadowF5 = shadowData.Params5;
    ivec4 shadowI2 = shadowData.Packed2;
    vec4 shadowF6 = shadowData.Params6;

    int debugMode = shadowI1.y;
    vec3 lightToFragBase = fragPos - light.Position;
    float receiverDist = length(lightToFragBase);
    vec3 lightDirN = receiverDist > 1e-6 ? -lightToFragBase / receiverDist : vec3(0.0, 0.0, 1.0);
    float nearPlaneDist = max(shadowF0.x, 0.0);
    float farPlaneDist = shadowF0.y;
    if (receiverDist >= farPlaneDist)
        return 1.0;

    float NoL = max(dot(normal, lightDirN), 0.0);
    float faceSize = max(float(textureSize(shadowMap, 0).x), 1.0);
    float texelDirectionSpan = 2.0 / faceSize;
    float normalOffset = receiverDist * texelDirectionSpan * XRENGINE_GetBiasParamNormalTexels(shadowF5);
    vec3 offsetPosWS = fragPos + normal * normalOffset;
    vec3 fragToLight = offsetPosWS - light.Position;
    float lightDist = length(fragToLight);
    if (lightDist >= farPlaneDist)
        return 1.0;

    // The cubemap shadow cameras clip everything inside the near plane.
    // Geometry crossing that plane can create a synthetic blocker shell near the light,
    // so receivers in that blind zone should be treated as unshadowed.
    if (lightDist <= nearPlaneDist + normalOffset)
    {
        if (debugMode != 0)
            XRENGINE_TrySetForwardShadowDebug(debugMode, 1.0, nearPlaneDist / max(farPlaneDist, 0.001));
        return 1.0;
    }

    float NoLSafe = max(NoL, 0.05);
    float tanTheta = sqrt(max(1.0 - NoLSafe * NoLSafe, 0.0)) / NoLSafe;
    float filterTexels = max(1.0, clamp(shadowF1.z * 256.0, 0.0, 8.0));
    float texelRel = texelDirectionSpan * (
        XRENGINE_GetBiasParamDepthTexels(shadowF5) +
        XRENGINE_GetBiasParamSlopeTexels(shadowF5) * tanTheta * filterTexels);
    float r16fRel = 1.0 / 512.0;
    float relThreshold = max(texelRel, r16fRel);
    float compareBias = lightDist * relThreshold;
    float biasedLightDist = lightDist - compareBias;
    float sampleRadiusScale = texelDirectionSpan * max(1.0, clamp(shadowF1.z * 256.0, 0.0, 4.0));
    float blockerSearchRadius = texelDirectionSpan * max(1.0, clamp(shadowF1.w * 256.0, 0.0, 4.0));
    float minPenumbra = texelDirectionSpan * max(1.0, clamp(shadowF2.x * 256.0, 0.0, 4.0));
    float maxPenumbra = texelDirectionSpan * max(1.0, clamp(shadowF2.y * 256.0, 0.0, 4.0));
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        shadowF2.w);
    float contact = 1.0;
    if (shadowI1.z != 0)
    {
        contact = ForwardContactShadowsEnabled
            ? XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDirN,
                normalOffset,
                compareBias,
                shadowF2.w,
                contactSampleCount,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                shadowF3.w,
                shadowF4.x,
                viewDepth)
            : XRENGINE_SampleContactShadowCube(
                shadowMap,
                fragPos,
                normal,
                light.Position,
                normalOffset,
                compareBias,
                farPlaneDist,
                shadowF2.w,
                contactSampleCount,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                shadowF3.w,
                shadowF4.x,
                viewDepth);
    }

    int encoding = shadowI2.x;
    float shadow = 1.0;
    if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        float receiverDepth = clamp(lightDist / max(farPlaneDist, 0.001), 0.0, 1.0);
        float normalizedBias = compareBias / max(farPlaneDist, 0.001);
        float momentReceiverDepth = clamp(receiverDepth - min(normalizedBias, 0.01), 0.0, 1.0);
        bool useMomentMipmaps = shadowI2.y != 0;
        float momentMipLevel = XRENGINE_ResolveShadowMomentMipLevel(0.0, float(shadowI2.z), useMomentMipmaps);
        shadow = XRENGINE_SampleShadowMomentCube(
            shadowMap,
            normalize(fragToLight),
            momentReceiverDepth,
            encoding,
            shadowF6.x,
            shadowF6.y,
            shadowF6.z,
            shadowF6.w,
            momentMipLevel,
            useMomentMipmaps) * contact;
    }
    else
    {
        // The cubemap supplies large-scale visibility; contact shadows add only the
        // short-range receiver/detail term so cubemap debugging can still isolate
        // the filtered shadow value before this multiplier.
        shadow = XRENGINE_SampleShadowCubeFiltered(
            shadowMap,
            fragToLight,
            biasedLightDist,
            0.0,
            farPlaneDist,
            sampleRadiusScale,
            blockerSearchRadius,
            shadowI0.z,
            shadowI0.y,
            shadowI1.x,
            shadowF2.z,
            minPenumbra,
            maxPenumbra,
            shadowI0.w) * contact;
    }

    if (debugMode != 0)
    {
        float receiverDepth = clamp(lightDist / max(farPlaneDist, 0.001), 0.0, 1.0);
        float normalizedBias = compareBias / max(farPlaneDist, 0.001);
        float margin = encoding != XRENGINE_SHADOW_ENCODING_DEPTH
            ? XRENGINE_EstimateShadowMomentMargin(
                shadowMap,
                normalize(fragToLight),
                clamp(receiverDepth - min(normalizedBias, 0.01), 0.0, 1.0),
                encoding,
                shadowF6.z,
                shadowF6.w,
                XRENGINE_ResolveShadowMomentMipLevel(0.0, float(shadowI2.z), shadowI2.y != 0),
                shadowI2.y != 0)
            : (texture(shadowMap, normalize(fragToLight)).r * farPlaneDist - biasedLightDist) / max(farPlaneDist, 0.001);
        XRENGINE_TrySetForwardShadowDebug(debugMode, shadow, margin);
    }

    return shadow;
}

float XRENGINE_ReadShadowMapPointLegacySlot(
    int shadowSlot,
    ForwardPointShadowData shadowData,
    PointLight light,
    vec3 normal,
    vec3 fragPos)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_ReadShadowMapPointLegacySampler(PointLightShadowMaps[0], shadowData, light, normal, fragPos);
    case 1:
        return XRENGINE_ReadShadowMapPointLegacySampler(PointLightShadowMaps[1], shadowData, light, normal, fragPos);
    case 2:
        return XRENGINE_ReadShadowMapPointLegacySampler(PointLightShadowMaps[2], shadowData, light, normal, fragPos);
    case 3:
        return XRENGINE_ReadShadowMapPointLegacySampler(PointLightShadowMaps[3], shadowData, light, normal, fragPos);
    default:
        return 1.0;
    }
}

float XRENGINE_ReadShadowMapPoint(int lightIndex, PointLight light, vec3 normal, vec3 fragPos)
{
    if (lightIndex < 0 || lightIndex >= PointLightCount || lightIndex >= PointLightShadows.length())
        return 1.0;

    ForwardPointShadowData shadowData = PointLightShadows[lightIndex];
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;

    int shadowSlot = shadowI0.x;
    int debugMode = shadowI1.y;
    vec2 atlasFaceUv;
    int atlasFaceIndex = XRENGINE_SelectPointShadowAtlasFace(fragPos - light.Position, atlasFaceUv);
    ivec4 atlasI0 = shadowData.AtlasPacked0[atlasFaceIndex];
    bool atlasPath = (atlasI0.z > 0 && atlasI0.z != XRENGINE_SHADOW_FALLBACK_LEGACY) ||
        atlasI0.w >= 0 ||
        atlasI0.x != 0;
    if (atlasPath)
        return XRENGINE_ReadPointAtlasShadowMap(shadowData, light, normal, fragPos);

    if (shadowSlot < 0 || shadowSlot >= XRENGINE_MAX_FORWARD_POINT_SHADOW_SLOTS)
    {
        XRENGINE_TrySetForwardShadowDebug(debugMode, 1.0, 1.0);
        return 1.0;
    }

    return XRENGINE_ReadShadowMapPointLegacySlot(shadowSlot, shadowData, light, normal, fragPos);
}

float XRENGINE_LinearizeSpotShadowDepth01(float depth, float nearZ, float farZ)
{
    float n = max(nearZ, 0.0001);
    float f = max(farZ, n + 0.0001);
    float z = depth * 2.0 - 1.0;
    float linearZ = (2.0 * n * f) / (f + n - z * (f - n));
    return clamp((linearZ - n) / (f - n), 0.0, 1.0);
}

float XRENGINE_SampleSpotAtlasPage(
    int pageIndex,
    vec3 localLinearCoord,
    float receiverPerspectiveDepth,
    vec4 uvScaleBias,
    float localTexelSize,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount,
    float nearZ,
    float farZ)
{
    if (pageIndex < 0 || pageIndex >= XRENGINE_GetSpotShadowAtlasLayerCount())
        return 1.0;

    return XRENGINE_SampleLinearDepthShadowAtlasFilteredAsPerspective(
        SpotLightShadowAtlas,
        localLinearCoord,
        float(pageIndex),
        receiverPerspectiveDepth,
        uvScaleBias,
        localTexelSize,
        bias,
        blockerSamples,
        filterSamples,
        filterRadius,
        blockerSearchRadius,
        softMode,
        lightSourceRadius,
        minPenumbra,
        maxPenumbra,
        vogelTapCount,
        nearZ,
        farZ);
}

float XRENGINE_ReadSpotAtlasCenterDepth(int pageIndex, vec2 uv)
{
    if (pageIndex < 0 || pageIndex >= XRENGINE_GetSpotShadowAtlasLayerCount())
        return 1.0;

    return texture(SpotLightShadowAtlas, vec3(uv, float(pageIndex))).r;
}

float XRENGINE_ReadSpotContactShadowOnly(int lightIndex, vec3 lightDir, vec3 normal, vec3 fragPos)
{
    if (!ForwardContactShadowsEnabled || lightIndex < 0 || lightIndex >= SpotLightCount || lightIndex >= SpotLightShadows.length())
        return 1.0;

    ForwardSpotShadowData shadowData = SpotLightShadows[lightIndex];
    ivec4 shadowI1 = shadowData.Packed1;
    if (shadowI1.z == 0)
        return 1.0;

    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    float contactDistance = shadowF2.y;
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        contactDistance);

    return XRENGINE_SampleForwardContactShadowScreenSpace(
        fragPos,
        normal,
        lightDir,
        shadowF3.y,
        contactDistance * 0.001,
        contactDistance,
        contactSampleCount,
        shadowF2.z,
        shadowF2.w,
        shadowF3.x,
        shadowF3.y,
        shadowF3.z,
        viewDepth);
}

float XRENGINE_ReadSpotFallbackContactShadow(
    int lightIndex,
    int shadowSlot,
    bool legacyShadowSlotValid,
    ForwardSpotShadowData shadowData,
    SpotLight light,
    vec3 normal,
    vec3 fragPos,
    vec3 lightDir)
{
    ivec4 shadowI1 = shadowData.Packed1;
    if (shadowI1.z == 0)
        return 1.0;

    if (ForwardContactShadowsEnabled)
        return XRENGINE_ReadSpotContactShadowOnly(lightIndex, lightDir, normal, fragPos);

    if (!legacyShadowSlotValid)
        return 1.0;

    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    vec4 shadowF5 = shadowData.Params5;
    vec4 shadowF6 = shadowData.Params6;
    float axisDistance = XRENGINE_GetSpotDistanceAlongAxis(light, fragPos);
    if (axisDistance >= shadowF5.y)
        return 1.0;

    float localTexelSize = XRENGINE_GetSpotShadowTexelSizeForSlot(shadowSlot);
    float worldTexelSize = XRENGINE_GetSpotShadowWorldTexelSize(light, fragPos, localTexelSize);
    float receiverOffset = worldTexelSize * XRENGINE_GetBiasParamNormalTexels(shadowF6);
    float constantBias = XRENGINE_PerspectiveDepthBiasForWorldOffset(
        worldTexelSize * XRENGINE_GetBiasParamDepthTexels(shadowF6),
        axisDistance,
        shadowF5.x,
        shadowF5.y);
    vec3 offsetPosWS = fragPos + normal * receiverOffset;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(light.Base.Base.WorldToLightSpaceProjMatrix, offsetPosWS);
    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return 1.0;

    float bias = XRENGINE_ComputeShadowDepthBias(
        fragCoord,
        vec2(localTexelSize),
        shadowF1.x,
        constantBias,
        XRENGINE_GetBiasParamSlopeTexels(shadowF6));
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        shadowF2.y);

    return XRENGINE_SampleSpotContactShadow2DSlot(
        shadowSlot,
        light.Base.Base.WorldToLightSpaceProjMatrix,
        fragPos,
        normal,
        lightDir,
        receiverOffset,
        bias,
        shadowF2.y,
        contactSampleCount,
        shadowF2.z,
        shadowF2.w,
        shadowF3.x,
        shadowF3.y,
        shadowF3.z,
        viewDepth);
}

float XRENGINE_ReadShadowMapSpotLegacySampler(
    sampler2D shadowMap,
    ForwardSpotShadowData shadowData,
    SpotLight light,
    vec3 normal,
    vec3 fragPos,
    vec3 lightDir)
{
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;
    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    ivec4 shadowI2 = shadowData.Packed2;
    vec4 shadowF4 = shadowData.Params4;
    vec4 shadowF5 = shadowData.Params5;
    vec4 shadowF6 = shadowData.Params6;

    int debugMode = shadowI1.y;
    float nearZ = shadowF5.x;
    float farZ = shadowF5.y;
    float axisDistance = XRENGINE_GetSpotDistanceAlongAxis(light, fragPos);
    if (axisDistance >= farZ)
        return 1.0;

    float localTexelSize = 1.0 / max(float(textureSize(shadowMap, 0).x), 1.0);
    float cosOuter = clamp(light.OuterCutoff, 0.001, 0.999999);
    float tanOuter = sqrt(max(1.0 - cosOuter * cosOuter, 0.0)) / cosOuter;
    float worldTexelSize = 2.0 * axisDistance * tanOuter * max(localTexelSize, 1e-7);
    float receiverOffset = worldTexelSize * XRENGINE_GetBiasParamNormalTexels(shadowF6);
    float constantBias = XRENGINE_PerspectiveDepthBiasForWorldOffset(
        worldTexelSize * XRENGINE_GetBiasParamDepthTexels(shadowF6),
        axisDistance,
        nearZ,
        farZ);
    vec3 offsetPosWS = fragPos + normal * receiverOffset;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(light.Base.Base.WorldToLightSpaceProjMatrix, offsetPosWS);
    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return 1.0;

    float bias = XRENGINE_ComputeShadowDepthBias(
        fragCoord,
        vec2(localTexelSize),
        shadowF1.x,
        constantBias,
        XRENGINE_GetBiasParamSlopeTexels(shadowF6));
    float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        shadowF2.y);
    float contact = 1.0;
    if (shadowI1.z != 0)
    {
        if (ForwardContactShadowsEnabled)
        {
            contact = XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDir,
                receiverOffset,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
        }
        else
        {
            contact = XRENGINE_SampleContactShadow2D(
                shadowMap,
                light.Base.Base.WorldToLightSpaceProjMatrix,
                fragPos,
                normal,
                lightDir,
                receiverOffset,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
        }
    }

    int encoding = shadowI2.x;
    float shadow = 1.0;
    if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        float receiverDepth = XRENGINE_LinearizeShadowDepth01(fragCoord.z, shadowF5.x, shadowF5.y);
        float momentReceiverDepth = clamp(receiverDepth - min(bias, 0.01), 0.0, 1.0);
        bool useMomentMipmaps = shadowI2.y != 0;
        float momentMipLevel = XRENGINE_ResolveShadowMomentMipLevel(
            shadowF5.z,
            float(shadowI2.z),
            useMomentMipmaps);
        shadow = XRENGINE_SampleShadowMoment2D(
            shadowMap,
            fragCoord.xy,
            momentReceiverDepth,
            encoding,
            shadowF4.x,
            shadowF4.y,
            shadowF4.z,
            shadowF4.w,
            momentMipLevel,
            useMomentMipmaps) * contact;
    }
    else
    {
        shadow = XRENGINE_SampleShadowMapFiltered(
            shadowMap,
            fragCoord,
            bias,
            shadowI0.z,
            shadowI0.y,
            shadowF1.x,
            shadowF1.y,
            shadowI1.x,
            shadowF2.x,
            shadowF1.z,
            shadowF1.w,
            shadowI0.w) * contact;
    }

    if (debugMode != 0)
    {
        float margin = encoding != XRENGINE_SHADOW_ENCODING_DEPTH
            ? XRENGINE_EstimateShadowMomentMargin(
                shadowMap,
                fragCoord.xy,
                clamp(XRENGINE_LinearizeShadowDepth01(fragCoord.z, shadowF5.x, shadowF5.y) - min(bias, 0.01), 0.0, 1.0),
                encoding,
                shadowF4.z,
                shadowF4.w,
                XRENGINE_ResolveShadowMomentMipLevel(shadowF5.z, float(shadowI2.z), shadowI2.y != 0),
                shadowI2.y != 0)
            : texture(shadowMap, fragCoord.xy).r - (fragCoord.z - bias);
        XRENGINE_TrySetForwardShadowDebug(debugMode, shadow, margin);
    }

    return shadow;
}

float XRENGINE_ReadShadowMapSpotLegacySlot(
    int shadowSlot,
    ForwardSpotShadowData shadowData,
    SpotLight light,
    vec3 normal,
    vec3 fragPos,
    vec3 lightDir)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_ReadShadowMapSpotLegacySampler(SpotLightShadowMaps[0], shadowData, light, normal, fragPos, lightDir);
    case 1:
        return XRENGINE_ReadShadowMapSpotLegacySampler(SpotLightShadowMaps[1], shadowData, light, normal, fragPos, lightDir);
    case 2:
        return XRENGINE_ReadShadowMapSpotLegacySampler(SpotLightShadowMaps[2], shadowData, light, normal, fragPos, lightDir);
    case 3:
        return XRENGINE_ReadShadowMapSpotLegacySampler(SpotLightShadowMaps[3], shadowData, light, normal, fragPos, lightDir);
    default:
        return 1.0;
    }
}

float XRENGINE_ReadShadowMapSpot(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 lightDir)
{
    if (lightIndex < 0 || lightIndex >= SpotLightCount || lightIndex >= SpotLightShadows.length())
        return 1.0;

    ForwardSpotShadowData shadowData = SpotLightShadows[lightIndex];
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;
    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    ivec4 shadowI2 = shadowData.Packed2;
    vec4 shadowF4 = shadowData.Params4;
    vec4 atlasUvScaleBias = shadowData.AtlasParams0;
    vec4 atlasDepthParams = shadowData.AtlasParams1;
    vec4 shadowF6 = shadowData.Params6;
    ivec4 atlasI0 = shadowData.AtlasPacked0;

    int shadowSlot = shadowI0.x;
    int debugMode = shadowI1.y;
    bool legacyShadowSlotValid = shadowSlot >= 0 && shadowSlot < XRENGINE_MAX_FORWARD_SPOT_SHADOW_SLOTS;
    bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < XRENGINE_GetSpotShadowAtlasLayerCount();
    int fallbackMode = atlasI0.z;
    bool atlasPath = (fallbackMode > 0 && fallbackMode != XRENGINE_SHADOW_FALLBACK_LEGACY) ||
        atlasI0.w >= 0 ||
        atlasI0.x != 0;

    if (atlasEnabled)
    {
        float nearZ = atlasDepthParams.x;
        float farZ = atlasDepthParams.y;
        float axisDistance = XRENGINE_GetSpotDistanceAlongAxis(light, fragPos);
        if (axisDistance >= farZ)
            return 1.0;

        float atlasResolutionScale = max(atlasDepthParams.w, 1.0);
        float localTexelSize = max(atlasDepthParams.z, 1e-7);
        float authoredTexelSize = max(localTexelSize / atlasResolutionScale, 1e-7);
        float worldTexelSize = XRENGINE_GetSpotShadowWorldTexelSize(light, fragPos, authoredTexelSize);
        float receiverOffset = worldTexelSize * XRENGINE_GetBiasParamNormalTexels(shadowF6);
        float constantBias = XRENGINE_PerspectiveDepthBiasForWorldOffset(
            worldTexelSize * XRENGINE_GetBiasParamDepthTexels(shadowF6),
            axisDistance,
            nearZ,
            farZ);
        vec3 offsetPosWS = fragPos + normal * receiverOffset;
        vec3 fragCoord = XRENGINE_ProjectShadowCoord(light.Base.Base.WorldToLightSpaceProjMatrix, offsetPosWS);
        if (!XRENGINE_ShadowCoordInBounds(fragCoord))
            return 1.0;

        float viewDepth = XRENGINE_GetForwardResolvedViewDepth(fragPos);
        int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
            shadowI1.w,
            viewDepth,
            shadowF2.y);
        float atlasBias = XRENGINE_ComputeShadowDepthBias(
            fragCoord,
            vec2(authoredTexelSize),
            shadowF1.x,
            constantBias,
            XRENGINE_GetBiasParamSlopeTexels(shadowF6));
        float contact = 1.0;
        if (shadowI1.z != 0 && ForwardContactShadowsEnabled)
        {
            contact = XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDir,
                receiverOffset,
                atlasBias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
        }

        vec2 atlasUv = fragCoord.xy * atlasUvScaleBias.xy + atlasUvScaleBias.zw;
        float atlasDepth = XRENGINE_LinearizeSpotShadowDepth01(
            fragCoord.z,
            nearZ,
            farZ);
        int encoding = shadowI2.x;
        if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
        {
            vec2 momentAtlasUv = XRENGINE_ShadowAtlasUvFromLocal(fragCoord.xy, atlasUvScaleBias);
            float momentReceiverDepth = clamp(atlasDepth - min(atlasBias, 0.01), 0.0, 1.0);
            float momentShadow = XRENGINE_SampleShadowMoment2DArray(
                SpotLightShadowAtlas,
                momentAtlasUv,
                float(atlasI0.y),
                momentReceiverDepth,
                encoding,
                shadowF4.x,
                shadowF4.y,
                shadowF4.z,
                shadowF4.w,
                0.0,
                false) * contact;

            if (debugMode != 0)
            {
                float margin = XRENGINE_EstimateShadowMomentMargin(
                    SpotLightShadowAtlas,
                    momentAtlasUv,
                    float(atlasI0.y),
                    momentReceiverDepth,
                    encoding,
                    shadowF4.z,
                    shadowF4.w,
                    0.0,
                    false);
                XRENGINE_TrySetForwardShadowDebug(debugMode, momentShadow, margin);
            }

            return momentShadow;
        }

        float shadow = XRENGINE_SampleSpotAtlasPage(
            atlasI0.y,
            vec3(fragCoord.xy, atlasDepth),
            fragCoord.z,
            atlasUvScaleBias,
            authoredTexelSize,
            atlasBias,
            shadowI0.z,
            shadowI0.y,
            shadowF1.x,
            shadowF1.y,
            shadowI1.x,
            shadowF2.x,
            shadowF1.z,
            shadowF1.w,
            shadowI0.w,
            nearZ,
            farZ) * contact;

        if (debugMode != 0)
        {
            float centerDepth = XRENGINE_LinearDepth01ToPerspectiveDepth(
                XRENGINE_ReadSpotAtlasCenterDepth(atlasI0.y, atlasUv),
                nearZ,
                farZ);
            float margin = centerDepth - (fragCoord.z - atlasBias);
            XRENGINE_TrySetForwardShadowDebug(debugMode, shadow, margin);
        }

        return shadow;
    }

    if (atlasPath)
    {
        float fallback = fallbackMode == XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY
            ? XRENGINE_ReadSpotFallbackContactShadow(lightIndex, shadowSlot, legacyShadowSlotValid, shadowData, light, normal, fragPos, lightDir)
            : 1.0;
        XRENGINE_TrySetForwardShadowDebug(debugMode, fallback, 1.0);
        return fallback;
    }

    if (!legacyShadowSlotValid)
    {
        float fallback = fallbackMode == XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY
            ? XRENGINE_ReadSpotFallbackContactShadow(lightIndex, shadowSlot, legacyShadowSlotValid, shadowData, light, normal, fragPos, lightDir)
            : 1.0;
        XRENGINE_TrySetForwardShadowDebug(debugMode, fallback, 1.0);
        return fallback;
    }

    return XRENGINE_ReadShadowMapSpotLegacySlot(shadowSlot, shadowData, light, normal, fragPos, lightDir);
}

vec3 XRENGINE_CalcPointLightWithViewDir(int lightIndex, PointLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, vec3 viewDir)
{
    vec3 lightVector = light.Position - fragPos;
    float attenuation = XRENGINE_Attenuate(length(lightVector), light.Radius) * light.Brightness;
    float shadow = XRENGINE_ReadShadowMapPoint(lightIndex, light, normal, fragPos);
    return XRENGINE_CalculateDirectPbrLightWithViewDir(light.Base.Color, light.Base.DiffuseIntensity, normalize(lightVector), normal, fragPos, albedo, rms, F0, attenuation, viewDir) * shadow;
}

vec3 XRENGINE_CalcPointLight(int lightIndex, PointLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalcPointLightWithViewDir(lightIndex, light, normal, fragPos, albedo, rms, F0, viewDir);
}

vec3 XRENGINE_CalcSpotLightWithViewDir(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, vec3 viewDir)
{
    vec3 lightVector = light.Base.Position - fragPos;
    vec3 lightDir = normalize(lightVector);
    float clampedCosine = max(0.0, dot(-lightDir, normalize(light.Direction)));
    float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);
    float spotAttn = pow(clampedCosine, light.Exponent);
    float distAttn = XRENGINE_Attenuate(length(lightVector), light.Base.Radius) * light.Base.Brightness;
    float shadow = XRENGINE_ReadShadowMapSpot(lightIndex, light, normal, fragPos, lightDir);
    return spotEffect * spotAttn * XRENGINE_CalculateDirectPbrLightWithViewDir(light.Base.Base.Color, light.Base.Base.DiffuseIntensity, lightDir, normal, fragPos, albedo, rms, F0, distAttn, viewDir) * shadow;
}

vec3 XRENGINE_CalcSpotLight(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalcSpotLightWithViewDir(lightIndex, light, normal, fragPos, albedo, rms, F0, viewDir);
}

vec3 XRENGINE_CalcForwardPlusColorWithViewDir(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation, vec3 viewDir)
{
    return XRENGINE_CalculateDirectPbrLightWithViewDir(lightColor, diffuseIntensity, lightDirection, normal, fragPos, albedo, rms, F0, attenuation, viewDir);
}

vec3 XRENGINE_CalcForwardPlusColor(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalcForwardPlusColorWithViewDir(lightColor, diffuseIntensity, lightDirection, normal, fragPos, albedo, rms, F0, attenuation, viewDir);
}

vec3 XRENGINE_CalcForwardPlusPointLightWithViewDir(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, vec3 viewDir)
{
    vec3 lightVector = light.PositionWS.xyz - fragPos;
    float attenuation = XRENGINE_Attenuate(length(lightVector), light.Params.x) * max(light.Params.y, 0.0001);
    int sourceIndex = light.Indices.x >= 0 ? light.Indices.x : int(light.Params.w + 0.5);
    float shadow = (sourceIndex >= 0 && sourceIndex < PointLightCount)
        ? XRENGINE_ReadShadowMapPoint(sourceIndex, PointLights[sourceIndex], normal, fragPos)
        : 1.0;
    return XRENGINE_CalcForwardPlusColorWithViewDir(light.Color_Type.xyz, light.Params.z, normalize(lightVector), normal, fragPos, albedo, rms, F0, attenuation, viewDir) * shadow;
}

vec3 XRENGINE_CalcForwardPlusPointLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalcForwardPlusPointLightWithViewDir(light, normal, fragPos, albedo, rms, F0, viewDir);
}

vec3 XRENGINE_CalcForwardPlusSpotLightWithViewDir(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, vec3 viewDir)
{
    vec3 lightDir = normalize(light.DirectionWS_Exponent.xyz);
    vec3 lightVector = light.PositionWS.xyz - fragPos;
    vec3 lightToPosN = normalize(lightVector);
    float clampedCosine = max(0.0, dot(-lightToPosN, lightDir));
    float spotEffect = smoothstep(light.SpotAngles.y, light.SpotAngles.x, clampedCosine);

    float spotAttn = pow(clampedCosine, light.DirectionWS_Exponent.w);
    float distAttn = XRENGINE_Attenuate(length(lightVector), light.Params.x) * max(light.Params.y, 0.0001);
    int sourceIndex = light.Indices.x >= 0 ? light.Indices.x : int(light.Params.w + 0.5);
    float shadow = (sourceIndex >= 0 && sourceIndex < SpotLightCount)
        ? XRENGINE_ReadShadowMapSpot(sourceIndex, SpotLights[sourceIndex], normal, fragPos, lightToPosN)
        : 1.0;

    return spotEffect * spotAttn * XRENGINE_CalcForwardPlusColorWithViewDir(light.Color_Type.xyz, light.Params.z, lightToPosN, normal, fragPos, albedo, rms, F0, distAttn, viewDir) * shadow;
}

vec3 XRENGINE_CalcForwardPlusSpotLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    return XRENGINE_CalcForwardPlusSpotLightWithViewDir(light, normal, fragPos, albedo, rms, F0, viewDir);
}

// Main lighting calculation function
// Call this from your fragment shader main() with your surface parameters
vec3 XRENGINE_CalculateForwardLighting(vec3 normal, vec3 fragPos, vec3 albedo, float specularIntensity, float ambientOcclusion)
{
    XRENGINE_BeginForwardLightingFragment(fragPos);
    XRENGINE_ForwardShadowDebugModeActive = 0;
    XRENGINE_ForwardShadowDebugColor = vec3(0.0);
    normal = normalize(normal);
    vec3 viewDir = normalize(XRENGINE_GetForwardResolvedCameraPosition() - fragPos);
    vec3 rms = vec3(clamp(Roughness, 0.0, 1.0), clamp(Metallic, 0.0, 1.0), max(specularIntensity, 0.0));
    vec3 F0 = mix(vec3(0.04), albedo, rms.y);
    vec3 totalLight = vec3(0.0);

    // Directional lights (up to the forward directional shadow slot count can cast shadows)
    for (int i = 0; i < DirLightCount; ++i)
    {
        bool useShadow = (i < XRENGINE_MAX_FORWARD_DIRECTIONAL_LIGHTS);
        totalLight += XRENGINE_CalcDirLightWithViewDir(i, DirectionalLights[i], normal, fragPos, albedo, rms, F0, useShadow, viewDir);
    }

    // Local lights: use Forward+ if available, otherwise brute-force
    if (ForwardPlusEnabled)
    {
        int tileCountX = ForwardPlusTileCountX;
        int tileCountY = ForwardPlusTileCountY;
        ivec2 tileCoord = ivec2(floor(gl_FragCoord.xy - ScreenOrigin)) / ForwardPlusTileSize;
        tileCoord = clamp(tileCoord, ivec2(0), ivec2(tileCountX - 1, tileCountY - 1));
        int tileIndex = XRENGINE_GetForwardResolvedViewIndex() * (tileCountX * tileCountY) + tileCoord.y * tileCountX + tileCoord.x;
        int baseIndex = tileIndex * ForwardPlusMaxLightsPerTile;

        for (int o = 0; o < ForwardPlusMaxLightsPerTile; ++o)
        {
            int lightIndex = ForwardPlusVisibleIndices[baseIndex + o];
            if (lightIndex < 0)
                break;

            ForwardPlusLocalLight l = ForwardPlusLocalLights[lightIndex];
            totalLight += (l.Color_Type.w < 0.5)
                ? XRENGINE_CalcForwardPlusPointLightWithViewDir(l, normal, fragPos, albedo, rms, F0, viewDir)
                : XRENGINE_CalcForwardPlusSpotLightWithViewDir(l, normal, fragPos, albedo, rms, F0, viewDir);
        }
    }
    else
    {
        for (int i = 0; i < PointLightCount; ++i)
            totalLight += XRENGINE_CalcPointLightWithViewDir(i, PointLights[i], normal, fragPos, albedo, rms, F0, viewDir);

        for (int i = 0; i < SpotLightCount; ++i)
            totalLight += XRENGINE_CalcSpotLightWithViewDir(i, SpotLights[i], normal, fragPos, albedo, rms, F0, viewDir);
    }

    if (XRENGINE_ForwardShadowDebugModeActive != 0)
        return XRENGINE_ForwardShadowDebugColor;

    return totalLight + XRENGINE_CalculateAmbientPbr(normal, fragPos, albedo, viewDir, rms, ambientOcclusion) + albedo * Emission;
}

#undef ShadowBlockerSamples
#undef ShadowFilterSamples
#undef ShadowVogelTapCount
#undef SoftShadowMode
#undef EnableCascadedShadows
#undef EnableContactShadows
#undef ContactShadowSamples
#undef ShadowBase
#undef ShadowMult
#undef ShadowBiasMin
#undef ShadowBiasMax
#undef ShadowFilterRadius
#undef ShadowBlockerSearchRadius
#undef ShadowMinPenumbra
#undef ShadowMaxPenumbra
#undef LightSourceRadius
#undef ContactShadowDistance
#undef ContactShadowThickness
#undef ContactShadowFadeStart
#undef ContactShadowFadeEnd
#undef ContactShadowNormalOffset
#undef ContactShadowJitterStrength
#undef ShadowDepthBiasTexels
#undef ShadowSlopeBiasTexels
#undef ShadowNormalBiasTexels
