#version 450

#ifdef XRENGINE_STEREO_DEFERRED
#extension GL_OVR_multiview2 : require
#endif

#ifndef XRENGINE_DEPTH_MODE_UNIFORM
#define XRENGINE_DEPTH_MODE_UNIFORM
uniform int DepthMode;
#endif

#pragma snippet "NormalEncoding"
#pragma snippet "LightAttenuation"
#pragma snippet "ShadowSampling"
#pragma snippet "PBRFunctions"
#pragma snippet "DepthUtils"
const int MAX_CASCADES = 8;
const int XRENGINE_SHADOW_FALLBACK_LIT = 1;
const int XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY = 2;
const int XRENGINE_SHADOW_FALLBACK_STALE_TILE = 3;
const int XRENGINE_SHADOW_FALLBACK_LEGACY = 5;

layout(location = 0) out vec3 OutColor; //Diffuse lighting output
layout(location = 0) in vec3 FragPos;

#ifdef XRENGINE_MSAA_DEFERRED
layout(binding = 0) uniform sampler2DMS AlbedoOpacity;
layout(binding = 1) uniform sampler2DMS Normal;
layout(binding = 2) uniform sampler2DMS RMSE;
layout(binding = 3) uniform sampler2DMS DepthView;
#elif defined(XRENGINE_STEREO_DEFERRED)
layout(binding = 0) uniform sampler2DArray AlbedoOpacity; //AlbedoOpacity
layout(binding = 1) uniform sampler2DArray Normal; //Normal
layout(binding = 2) uniform sampler2DArray RMSE; //PBR: Roughness, Metallic, Specular, Index of refraction
layout(binding = 3) uniform sampler2DArray DepthView; //Depth
#else
layout(binding = 0) uniform sampler2D AlbedoOpacity; //AlbedoOpacity
layout(binding = 1) uniform sampler2D Normal; //Normal
layout(binding = 2) uniform sampler2D RMSE; //PBR: Roughness, Metallic, Specular, Index of refraction
layout(binding = 3) uniform sampler2D DepthView; //Depth
#endif
uniform sampler2D ShadowMap; //Directional Shadow Map
uniform sampler2DArray ShadowMapArray; //Directional Cascaded Shadow Map
layout(binding = 9) uniform sampler2DArray DirectionalShadowAtlas;
uniform bool UseCascadedDirectionalShadows = false;
uniform bool LightHasShadowMap = true;
uniform bool EnableCascadedShadows = true;
uniform bool DebugCascadeColors = false;
uniform bool DirectionalShadowAtlasEnabled = false;
uniform ivec4 DirectionalShadowAtlasPacked0[MAX_CASCADES]; // enabled, page, fallback, record index
uniform vec4 DirectionalShadowAtlasUvScaleBias[MAX_CASCADES];
uniform vec4 DirectionalShadowAtlasDepthParams[MAX_CASCADES]; // near, far, local texel size, requested/allocated scale
uniform float DirectionalShadowAtlasMaxStaleFrames = 32.0f;
uniform int DeferredDebugMode = 0;

// Distinct debug colors per cascade index
const vec3 CascadeDebugColorTable[MAX_CASCADES] = vec3[](
	vec3(1.0, 0.2, 0.2),  // Red
	vec3(0.2, 1.0, 0.2),  // Green
	vec3(0.3, 0.3, 1.0),  // Blue
	vec3(1.0, 1.0, 0.2),  // Yellow
	vec3(1.0, 0.5, 0.0),  // Orange
	vec3(0.8, 0.2, 1.0),  // Purple
	vec3(0.0, 1.0, 1.0),  // Cyan
	vec3(1.0, 0.5, 0.7)   // Pink
);

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 ViewProjectionMatrix;
#ifdef XRENGINE_STEREO_DEFERRED
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
#endif
uniform float ShadowBase = 0.035f;
uniform float ShadowMult = 1.221f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;
uniform vec4 ShadowBiasParams = vec4(1.0f, 2.0f, 1.0f, 0.0f); // depth texels, slope texels, normal texels, reserved
uniform vec4 ShadowBiasProjectionParams = vec4(0.0f, 0.0f, 0.0f, 0.0f); // constant depth bias, normal offset, world texel size, depth range
uniform int ShadowSamples = 8;
uniform int ShadowBlockerSamples = 8;
uniform int ShadowFilterSamples = 8;
uniform int ShadowVogelTapCount = 5;
uniform float ShadowFilterRadius = 0.0012f;
uniform float ShadowBlockerSearchRadius = 0.01f;
uniform float ShadowMinPenumbra = 0.001f;
uniform float ShadowMaxPenumbra = 0.015f;
uniform int SoftShadowMode = 2;
uniform float LightSourceRadius = 1.2f;
uniform bool EnableContactShadows = true;
uniform float ContactShadowDistance = 1.0f;
uniform int ContactShadowSamples = 16;
uniform float ContactShadowThickness = 2.0f;
uniform float ContactShadowFadeStart = 10.0f;
uniform float ContactShadowFadeEnd = 40.0f;
uniform float ContactShadowNormalOffset = 0.0f;
uniform float ContactShadowJitterStrength = 1.0f;
uniform vec4 ShadowMomentParams0 = vec4(0.00002f, 0.2f, 5.0f, 5.0f); // min variance, light bleed reduction, positive exponent, negative exponent
uniform vec4 ShadowMomentFilterParams = vec4(0.0f, 0.0f, 0.0f, 0.0f); // blur radius texels, blur passes, use mipmaps, mip bias

struct DirLight
{
	vec3 Color;
	float DiffuseIntensity;
	mat4 WorldToLightInvViewMatrix;
	mat4 WorldToLightProjMatrix;
	mat4 WorldToLightSpaceMatrix;  // Pre-computed View * Proj for shadow mapping
	vec3 Direction;
	float CascadeSplits[MAX_CASCADES];
	float CascadeBlendWidths[MAX_CASCADES];
	float CascadeBiasMin[MAX_CASCADES];
	float CascadeBiasMax[MAX_CASCADES];
	float CascadeReceiverOffsets[MAX_CASCADES];
	mat4 CascadeMatrices[MAX_CASCADES];
	float RenderedCascadeSplits[MAX_CASCADES];
	float RenderedCascadeBlendWidths[MAX_CASCADES];
	float RenderedCascadeBiasMin[MAX_CASCADES];
	float RenderedCascadeBiasMax[MAX_CASCADES];
	float RenderedCascadeReceiverOffsets[MAX_CASCADES];
	mat4 RenderedCascadeMatrices[MAX_CASCADES];
	float RenderedCascadeStaleAge[MAX_CASCADES];
	int CascadeCount;
};
uniform DirLight LightData;

int XRENGINE_ResolveContactShadowSampleCount(int requestedSamples, float viewDepth, float contactDistance);
#ifdef XRENGINE_MSAA_DEFERRED
float XRENGINE_SampleContactShadowScreenSpace(
	sampler2DMS sceneDepth,
	int sampleId,
	vec2 screenSize,
	mat4 viewMatrix,
	mat4 inverseProjMatrix,
	mat4 inverseViewMatrix,
	mat4 viewProjectionMatrix,
	int depthMode,
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
	float viewDepth);
#elif defined(XRENGINE_STEREO_DEFERRED)
float XRENGINE_SampleContactShadowScreenSpace(
	sampler2DArray sceneDepth,
	sampler2DArray sceneNormal,
	float layer,
	vec2 screenSize,
	mat4 viewMatrix,
	mat4 inverseProjMatrix,
	mat4 inverseViewMatrix,
	mat4 viewProjectionMatrix,
	int depthMode,
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
	float viewDepth);
#else
float XRENGINE_SampleContactShadowScreenSpace(
	sampler2D sceneDepth,
	vec2 screenSize,
	mat4 viewMatrix,
	mat4 inverseProjMatrix,
	mat4 inverseViewMatrix,
	mat4 viewProjectionMatrix,
	int depthMode,
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
	float viewDepth);
#endif

const vec2 LocalShadowPoissonDisk[16] = vec2[](
	vec2(-0.94201624, -0.39906216), vec2(0.94558609, -0.76890725),
	vec2(-0.09418410, -0.92938870), vec2(0.34495938, 0.29387760),
	vec2(-0.91588581, 0.45771432), vec2(-0.81544232, -0.87912464),
	vec2(-0.38277543, 0.27676845), vec2(0.97484398, 0.75648379),
	vec2(0.44323325, -0.97511554), vec2(0.53742981, -0.47373420),
	vec2(-0.26496911, -0.41893023), vec2(0.79197514, 0.19090188),
	vec2(-0.24188840, 0.99706507), vec2(-0.81409955, 0.91437590),
	vec2(0.19984126, 0.78641367), vec2(0.14383161, -0.14100790)
);

float SampleShadowMapSoftLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in int sampleCount, in float filterRadius)
{
	int clampedSamples = clamp(sampleCount, 1, 16);
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
	float radius = max(filterRadius, max(texelSize.x, texelSize.y));
	float lit = 0.0f;

	for (int i = 0; i < 16; ++i)
	{
		if (i >= clampedSamples)
			break;

		vec2 sampleUv = shadowCoord.xy + LocalShadowPoissonDisk[i] * radius;
		float sampleDepth = texture(shadowMap, sampleUv).r;
		lit += XRENGINE_ShadowLit(shadowCoord.z, sampleDepth, bias);
	}

	return lit / float(clampedSamples);
}

float SampleShadowMapPCFLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in int kernelSize)
{
	float lit = 0.0f;
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
	int halfKernel = kernelSize / 2;
	float sampleCount = float(kernelSize * kernelSize);

	for (int x = -halfKernel; x <= halfKernel; ++x)
	{
		for (int y = -halfKernel; y <= halfKernel; ++y)
		{
			float sampleDepth = texture(shadowMap, shadowCoord.xy + vec2(x, y) * texelSize).r;
			lit += XRENGINE_ShadowLit(shadowCoord.z, sampleDepth, bias);
		}
	}

	return lit / sampleCount;
}

float SampleShadowMapSimpleLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias)
{
	float depth = texture(shadowMap, shadowCoord.xy).r;
	return XRENGINE_ShadowLit(shadowCoord.z, depth, bias);
}

float SampleShadowMapTent4Local(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in float filterRadius)
{
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
	vec2 radius = max(vec2(max(filterRadius, 0.0f)), texelSize);
	float lit = 0.0f;

	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, shadowCoord.xy + vec2(-0.5f, -0.5f) * radius).r, bias);
	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, shadowCoord.xy + vec2(0.5f, -0.5f) * radius).r, bias);
	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, shadowCoord.xy + vec2(-0.5f, 0.5f) * radius).r, bias);
	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, shadowCoord.xy + vec2(0.5f, 0.5f) * radius).r, bias);

	return lit * 0.25f;
}

float SampleShadowMapArraySoftLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in int sampleCount, in float filterRadius)
{
	int clampedSamples = clamp(sampleCount, 1, 16);
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
	float radius = max(filterRadius, max(texelSize.x, texelSize.y));
	float lit = 0.0f;

	for (int i = 0; i < 16; ++i)
	{
		if (i >= clampedSamples)
			break;

		vec2 sampleUv = shadowCoord.xy + LocalShadowPoissonDisk[i] * radius;
		float sampleDepth = texture(shadowMap, vec3(sampleUv, layer)).r;
		lit += XRENGINE_ShadowLit(shadowCoord.z, sampleDepth, bias);
	}

	return lit / float(clampedSamples);
}

float SampleShadowMapArrayPCFLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in int kernelSize)
{
	float lit = 0.0f;
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
	int halfKernel = kernelSize / 2;
	float sampleCount = float(kernelSize * kernelSize);

	for (int x = -halfKernel; x <= halfKernel; ++x)
	{
		for (int y = -halfKernel; y <= halfKernel; ++y)
		{
			float sampleDepth = texture(shadowMap, vec3(shadowCoord.xy + vec2(x, y) * texelSize, layer)).r;
			lit += XRENGINE_ShadowLit(shadowCoord.z, sampleDepth, bias);
		}
	}

	return lit / sampleCount;
}

float SampleShadowMapArraySimpleLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias)
{
	float depth = texture(shadowMap, vec3(shadowCoord.xy, layer)).r;
	return XRENGINE_ShadowLit(shadowCoord.z, depth, bias);
}

float SampleShadowMapArrayTent4Local(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in float filterRadius)
{
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
	vec2 radius = max(vec2(max(filterRadius, 0.0f)), texelSize);
	float lit = 0.0f;

	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f, -0.5f) * radius, layer)).r, bias);
	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5f, -0.5f) * radius, layer)).r, bias);
	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f, 0.5f) * radius, layer)).r, bias);
	lit += XRENGINE_ShadowLit(shadowCoord.z, texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5f, 0.5f) * radius, layer)).r, bias);

	return lit * 0.25f;
}

float BlockerSearch2DLocal(in sampler2D shadowMap, in vec2 uv, in float receiverDepth, in float searchRadius, in int sampleCount)
{
	float blockerSum = 0.0f;
	int blockerCount = 0;
	int clampedSamples = clamp(sampleCount, 1, 16);
	for (int i = 0; i < 16; ++i)
	{
		if (i >= clampedSamples) break;
		vec2 sampleUv = uv + LocalShadowPoissonDisk[i] * searchRadius;
		float d = texture(shadowMap, sampleUv).r;
		if (XRENGINE_IsShadowBlocker(d, receiverDepth)) { blockerSum += d; blockerCount++; }
	}
	return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0f;
}

float BlockerSearch2DArrayLocal(in sampler2DArray shadowMap, in vec2 uv, in float layer, in float receiverDepth, in float searchRadius, in int sampleCount)
{
	float blockerSum = 0.0f;
	int blockerCount = 0;
	int clampedSamples = clamp(sampleCount, 1, 16);
	for (int i = 0; i < 16; ++i)
	{
		if (i >= clampedSamples) break;
		vec2 sampleUv = uv + LocalShadowPoissonDisk[i] * searchRadius;
		float d = texture(shadowMap, vec3(sampleUv, layer)).r;
		if (XRENGINE_IsShadowBlocker(d, receiverDepth)) { blockerSum += d; blockerCount++; }
	}
	return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0f;
}

float SampleShadowMapCHSSLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in int sampleCount, in float searchRadius, in float lightSourceRadius)
{
	float receiverDepth = XRENGINE_ApplyShadowBias(shadowCoord.z, bias);
	float avgBlocker = BlockerSearch2DLocal(shadowMap, shadowCoord.xy, receiverDepth, searchRadius, sampleCount);
	if (avgBlocker < 0.0f) return 1.0f;
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
	float minR = max(texelSize.x, texelSize.y);
	float penumbra = clamp(abs(receiverDepth - avgBlocker) / max(abs(avgBlocker), 0.0001f) * lightSourceRadius, minR, searchRadius * 4.0f);
	return SampleShadowMapSoftLocal(shadowMap, shadowCoord, bias, sampleCount, penumbra);
}

float SampleShadowMapArrayCHSSLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in int sampleCount, in float searchRadius, in float lightSourceRadius)
{
	float receiverDepth = XRENGINE_ApplyShadowBias(shadowCoord.z, bias);
	float avgBlocker = BlockerSearch2DArrayLocal(shadowMap, shadowCoord.xy, layer, receiverDepth, searchRadius, sampleCount);
	if (avgBlocker < 0.0f) return 1.0f;
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
	float minR = max(texelSize.x, texelSize.y);
	float penumbra = clamp(abs(receiverDepth - avgBlocker) / max(abs(avgBlocker), 0.0001f) * lightSourceRadius, minR, searchRadius * 4.0f);
	return SampleShadowMapArraySoftLocal(shadowMap, shadowCoord, layer, bias, sampleCount, penumbra);
}

float SampleShadowMapFilteredLocal(
	in sampler2D shadowMap,
	in vec3 shadowCoord,
	in float bias,
	in int blockerSamples,
	in int filterSamples,
	in float filterRadius,
	in float blockerSearchRadius,
	in int softMode,
	in float lightSourceRadius,
	in float minPenumbra,
	in float maxPenumbra,
	in int vogelTapCount)
{
	return XRENGINE_SampleShadowMapFiltered(
		shadowMap,
		shadowCoord,
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

float SampleShadowMapArrayFilteredLocal(
	in sampler2DArray shadowMap,
	in vec3 shadowCoord,
	in float layer,
	in float bias,
	in int blockerSamples,
	in int filterSamples,
	in float filterRadius,
	in float blockerSearchRadius,
	in int softMode,
	in float lightSourceRadius,
	in float minPenumbra,
	in float maxPenumbra,
	in int vogelTapCount)
{
	return XRENGINE_SampleShadowMapArrayFiltered(
		shadowMap,
		shadowCoord,
		layer,
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

float GetShadowBiasRange(in float NoL, in float biasMin, in float biasMax)
{
	float mapped = pow(max(ShadowBase, 0.0f) * (1.0f - max(NoL, 0.0f)), max(ShadowMult, 0.0001f));
	return mix(biasMin, biasMax, clamp(mapped, 0.0f, 1.0f));
}

float GetShadowBias(in float NoL)
{
	return GetShadowBiasRange(NoL, ShadowBiasMin, ShadowBiasMax);
}

float GetDeferredContactShadowCompareBias()
{
	// Contact shadows compare camera-space depths, so keep the tolerance in world
	// units instead of inheriting cascade/shadow-map texel bias.
	return max(ContactShadowDistance * 0.001f, 0.0001f);
}

float SampleDeferredContactShadow(in vec3 fragPosWS, in vec3 N, in vec3 lightDirWS, in float viewDepth)
{
	int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
		ContactShadowSamples,
		viewDepth,
		ContactShadowDistance);
	float contactCompareBias = GetDeferredContactShadowCompareBias();
#ifdef XRENGINE_MSAA_DEFERRED
	return XRENGINE_SampleContactShadowScreenSpace(
		DepthView,
		gl_SampleID,
		vec2(ScreenWidth, ScreenHeight),
		ViewMatrix,
		InverseProjMatrix,
		InverseViewMatrix,
		ViewProjectionMatrix,
		DepthMode,
		fragPosWS,
		N,
		lightDirWS,
		0.0f,
		contactCompareBias,
		ContactShadowDistance,
		contactSampleCount,
		ContactShadowThickness,
		ContactShadowFadeStart,
		ContactShadowFadeEnd,
		ContactShadowNormalOffset,
		ContactShadowJitterStrength,
		viewDepth);
#elif defined(XRENGINE_STEREO_DEFERRED)
	bool leftEye = gl_ViewID_OVR == 0;
	mat4 viewMatrix = leftEye ? LeftEyeViewMatrix : RightEyeViewMatrix;
	mat4 inverseProjMatrix = leftEye ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
	mat4 inverseViewMatrix = leftEye ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
	mat4 viewProjectionMatrix = leftEye ? LeftEyeViewProjectionMatrix : RightEyeViewProjectionMatrix;
	return XRENGINE_SampleContactShadowScreenSpace(
		DepthView,
		Normal,
		float(gl_ViewID_OVR),
		vec2(ScreenWidth, ScreenHeight),
		viewMatrix,
		inverseProjMatrix,
		inverseViewMatrix,
		viewProjectionMatrix,
		DepthMode,
		fragPosWS,
		N,
		lightDirWS,
		0.0f,
		contactCompareBias,
		ContactShadowDistance,
		contactSampleCount,
		ContactShadowThickness,
		ContactShadowFadeStart,
		ContactShadowFadeEnd,
		ContactShadowNormalOffset,
		ContactShadowJitterStrength,
		viewDepth);
#else
	return XRENGINE_SampleContactShadowScreenSpace(
		DepthView,
		vec2(ScreenWidth, ScreenHeight),
		ViewMatrix,
		InverseProjMatrix,
		InverseViewMatrix,
		ViewProjectionMatrix,
		DepthMode,
		fragPosWS,
		N,
		lightDirWS,
		0.0f,
		contactCompareBias,
		ContactShadowDistance,
		contactSampleCount,
		ContactShadowThickness,
		ContactShadowFadeStart,
		ContactShadowFadeEnd,
		ContactShadowNormalOffset,
		ContactShadowJitterStrength,
		viewDepth);
#endif
}

float SampleDirectionalAtlasPage(
	in int pageIndex,
	in vec3 localCoord,
	in vec4 uvScaleBias,
	in float localTexelSize,
	in float bias,
	in int blockerSamples,
	in int filterSamples,
	in float filterRadius,
	in float blockerSearchRadius,
	in int softMode,
	in float lightSourceRadius,
	in float minPenumbra,
	in float maxPenumbra,
	in int vogelTapCount);

//0 is fully in shadow, 1 is fully lit
float ReadShadowMap2D(in vec3 fragPosWS, in vec3 N, in float NoL, in float viewDepth, in mat4 lightMatrix)
{
		if (!LightHasShadowMap)
			return 1.0f;

		float atlasResolutionScale = 1.0f;
		bool atlasMetadataEnabled = false;
		if (DirectionalShadowAtlasEnabled)
		{
			ivec4 atlasState = DirectionalShadowAtlasPacked0[0];
			atlasMetadataEnabled = atlasState.x != 0;
			if (atlasMetadataEnabled)
				atlasResolutionScale = max(DirectionalShadowAtlasDepthParams[0].w, 1.0f);
		}

		float receiverOffset = max(ShadowBiasProjectionParams.y, 0.0f);
		vec3 offsetPosWS = fragPosWS + N * receiverOffset;
		vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
			fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
			fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return 1.0f;

		vec2 shadowTexelSize = atlasMetadataEnabled && DirectionalShadowAtlasDepthParams[0].z > 0.0f
			? vec2(max(DirectionalShadowAtlasDepthParams[0].z / atlasResolutionScale, 1e-7f))
			: 1.0f / vec2(textureSize(ShadowMap, 0));
		float bias = XRENGINE_ComputeShadowDepthBias(
			fragCoord,
			shadowTexelSize,
			ShadowFilterRadius,
			ShadowBiasProjectionParams.x,
			max(ShadowBiasParams.y, 0.0f));
		float contact = EnableContactShadows
			? SampleDeferredContactShadow(fragPosWS, N, normalize(-LightData.Direction), viewDepth)
			: 1.0f;

		if (DirectionalShadowAtlasEnabled)
		{
			ivec4 atlasI0 = DirectionalShadowAtlasPacked0[0];
			bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < textureSize(DirectionalShadowAtlas, 0).z;
			if (atlasEnabled)
			{
				vec4 atlasUvScaleBias = DirectionalShadowAtlasUvScaleBias[0];
				float atlasLocalTexelSize = max(DirectionalShadowAtlasDepthParams[0].z / atlasResolutionScale, 1e-7f);
				float atlasBias = XRENGINE_ComputeShadowDepthBias(
					fragCoord,
					vec2(atlasLocalTexelSize),
					ShadowFilterRadius,
					ShadowBiasProjectionParams.x,
					max(ShadowBiasParams.y, 0.0f));
				if (ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH)
				{
					vec2 atlasUv = XRENGINE_ShadowAtlasUvFromLocal(fragCoord.xy, atlasUvScaleBias);
					float atlasMomentReceiverDepth = clamp(fragCoord.z - min(atlasBias, 0.01f), 0.0f, 1.0f);
					return XRENGINE_SampleShadowMoment2DArray(
						DirectionalShadowAtlas,
						atlasUv,
						float(atlasI0.y),
						atlasMomentReceiverDepth,
						ShadowMapEncoding,
						ShadowMomentParams0.x,
						ShadowMomentParams0.y,
						ShadowMomentParams0.z,
						ShadowMomentParams0.w,
						0.0f,
						false) * contact;
				}

				return SampleDirectionalAtlasPage(
					atlasI0.y,
					fragCoord,
					atlasUvScaleBias,
					atlasLocalTexelSize,
					atlasBias,
					ShadowBlockerSamples,
					ShadowFilterSamples,
					ShadowFilterRadius,
					ShadowBlockerSearchRadius,
					SoftShadowMode,
					LightSourceRadius,
					ShadowMinPenumbra,
					ShadowMaxPenumbra,
					ShadowVogelTapCount) * contact;
			}

		}

		if (ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH)
		{
			float momentReceiverDepth = clamp(fragCoord.z - min(bias, 0.01f), 0.0f, 1.0f);
			bool useMomentMipmaps = ShadowMomentFilterParams.z != 0.0f;
			float momentMipLevel = XRENGINE_ResolveShadowMomentMipLevel(
				ShadowMomentFilterParams.w,
				ShadowMomentFilterParams.x,
				useMomentMipmaps);
			return XRENGINE_SampleShadowMoment2D(
				ShadowMap,
				fragCoord.xy,
				momentReceiverDepth,
				ShadowMapEncoding,
				ShadowMomentParams0.x,
				ShadowMomentParams0.y,
				ShadowMomentParams0.z,
				ShadowMomentParams0.w,
				momentMipLevel,
				useMomentMipmaps) * contact;
		}

		return SampleShadowMapFilteredLocal(
			ShadowMap,
			fragCoord,
			bias,
			ShadowBlockerSamples,
			ShadowFilterSamples,
			ShadowFilterRadius,
			ShadowBlockerSearchRadius,
			SoftShadowMode,
			LightSourceRadius,
			ShadowMinPenumbra,
			ShadowMaxPenumbra,
			ShadowVogelTapCount) * contact;
}

float SampleDirectionalAtlasPage(
	in int pageIndex,
	in vec3 localCoord,
	in vec4 uvScaleBias,
	in float localTexelSize,
	in float bias,
	in int blockerSamples,
	in int filterSamples,
	in float filterRadius,
	in float blockerSearchRadius,
	in int softMode,
	in float lightSourceRadius,
	in float minPenumbra,
	in float maxPenumbra,
	in int vogelTapCount)
{
	if (pageIndex < 0 || pageIndex >= textureSize(DirectionalShadowAtlas, 0).z)
		return 1.0f;

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

float ReadDirectionalAtlasCenterDepth(in int pageIndex, in vec2 uv)
{
	if (pageIndex >= 0 && pageIndex < textureSize(DirectionalShadowAtlas, 0).z)
		return texture(DirectionalShadowAtlas, vec3(uv, float(pageIndex))).r;
	return 1.0f;
}

float ApplyDirectionalStaleAtlasEdgeFade(in float shadow, in vec3 fragCoord, in float localTexelSize, in float staleAge)
{
	if (staleAge <= 0.0f)
		return shadow;

	float border = min(min(fragCoord.x, 1.0f - fragCoord.x), min(fragCoord.y, 1.0f - fragCoord.y));
	float fadeWidth = max(localTexelSize * 4.0f, 1e-5f);
	return mix(1.0f, shadow, smoothstep(0.0f, fadeWidth, border));
}

float ReadCascadeShadowMap(in vec3 fragPosWS, in vec3 N, in float NoL, in float viewDepth, in int cascadeIndex)
{
		if (!LightHasShadowMap)
			return 1.0f;

		ivec4 atlasState = ivec4(0, -1, XRENGINE_SHADOW_FALLBACK_LIT, -1);
		float atlasResolutionScale = 1.0f;
		bool atlasPageValid = false;
		bool atlasSampleAllowed = false;
		if (DirectionalShadowAtlasEnabled)
		{
			atlasState = DirectionalShadowAtlasPacked0[cascadeIndex];
			atlasPageValid = atlasState.x != 0 && atlasState.y >= 0 && atlasState.y < textureSize(DirectionalShadowAtlas, 0).z;
			float renderedAge = LightData.RenderedCascadeStaleAge[cascadeIndex];
			bool staleTileFallback = atlasState.z == XRENGINE_SHADOW_FALLBACK_STALE_TILE;
			atlasSampleAllowed = atlasPageValid &&
				renderedAge >= 0.0f &&
				(!staleTileFallback || renderedAge <= DirectionalShadowAtlasMaxStaleFrames);
			if (atlasSampleAllowed)
				atlasResolutionScale = max(DirectionalShadowAtlasDepthParams[cascadeIndex].w, 1.0f);
		}

		mat4 lightMatrix = atlasSampleAllowed ? LightData.RenderedCascadeMatrices[cascadeIndex] : LightData.CascadeMatrices[cascadeIndex];
		float receiverOffset = atlasSampleAllowed ? LightData.RenderedCascadeReceiverOffsets[cascadeIndex] : LightData.CascadeReceiverOffsets[cascadeIndex];
		vec3 offsetPosWS = fragPosWS + N * receiverOffset;
		vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
			fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
			fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return -1.0f;

		float cascadeScale = 1.0f + float(cascadeIndex) * 0.35f;
		float filterRadius = ShadowFilterRadius * cascadeScale;
		float atlasAuthoredTexelSize = DirectionalShadowAtlasDepthParams[cascadeIndex].z / atlasResolutionScale;
		float constantBias = atlasSampleAllowed ? LightData.RenderedCascadeBiasMin[cascadeIndex] : LightData.CascadeBiasMin[cascadeIndex];
		float maxBias = atlasSampleAllowed ? LightData.RenderedCascadeBiasMax[cascadeIndex] : LightData.CascadeBiasMax[cascadeIndex];
		vec2 shadowTexelSize = atlasSampleAllowed && DirectionalShadowAtlasDepthParams[cascadeIndex].z > 0.0f
			? vec2(max(atlasAuthoredTexelSize, 1e-7f))
			: 1.0f / vec2(textureSize(ShadowMapArray, 0).xy);
		float bias = XRENGINE_ComputeShadowDepthBias(
			fragCoord,
			shadowTexelSize,
			filterRadius,
			constantBias,
			maxBias);
		float contact = EnableContactShadows
			? SampleDeferredContactShadow(fragPosWS, N, normalize(-LightData.Direction), viewDepth)
			: 1.0f;

		bool useMomentMipmaps = ShadowMomentFilterParams.z != 0.0f;
		float momentMipLevel = XRENGINE_ResolveShadowMomentMipLevel(
			ShadowMomentFilterParams.w,
			ShadowMomentFilterParams.x,
			useMomentMipmaps);
		float momentReceiverDepth = clamp(fragCoord.z - min(bias, 0.01f), 0.0f, 1.0f);

		if (DirectionalShadowAtlasEnabled)
		{
			ivec4 atlasI0 = atlasState;
			int fallbackMode = atlasI0.z;
			if (atlasSampleAllowed)
			{
				vec4 atlasUvScaleBias = DirectionalShadowAtlasUvScaleBias[cascadeIndex];
				float atlasLocalTexelSize = max(atlasAuthoredTexelSize, 1e-7f);
				float atlasBias = XRENGINE_ComputeShadowDepthBias(
					fragCoord,
					vec2(atlasLocalTexelSize),
					filterRadius,
					constantBias,
					maxBias);
				if (ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH)
				{
					vec2 atlasUv = XRENGINE_ShadowAtlasUvFromLocal(fragCoord.xy, atlasUvScaleBias);
					float atlasMomentReceiverDepth = clamp(fragCoord.z - min(atlasBias, 0.01f), 0.0f, 1.0f);
					float shadow = XRENGINE_SampleShadowMoment2DArray(
						DirectionalShadowAtlas,
						atlasUv,
						float(atlasI0.y),
						atlasMomentReceiverDepth,
						ShadowMapEncoding,
						ShadowMomentParams0.x,
						ShadowMomentParams0.y,
						ShadowMomentParams0.z,
						ShadowMomentParams0.w,
						0.0f,
						false);
					return ApplyDirectionalStaleAtlasEdgeFade(shadow, fragCoord, atlasLocalTexelSize, LightData.RenderedCascadeStaleAge[cascadeIndex]) * contact;
				}

				float shadow = SampleDirectionalAtlasPage(
					atlasI0.y,
					fragCoord,
					atlasUvScaleBias,
					atlasLocalTexelSize,
					atlasBias,
					ShadowBlockerSamples,
					ShadowFilterSamples,
					filterRadius,
					ShadowBlockerSearchRadius * cascadeScale,
					SoftShadowMode,
					LightSourceRadius,
					ShadowMinPenumbra * cascadeScale,
					ShadowMaxPenumbra * cascadeScale,
					ShadowVogelTapCount);
				return ApplyDirectionalStaleAtlasEdgeFade(shadow, fragCoord, atlasLocalTexelSize, LightData.RenderedCascadeStaleAge[cascadeIndex]) * contact;
			}

			if (fallbackMode > 0 && fallbackMode != XRENGINE_SHADOW_FALLBACK_LEGACY)
				return fallbackMode == XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY ? contact : 1.0f;
		}

		if (ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH)
		{
			return XRENGINE_SampleShadowMoment2DArray(
				ShadowMapArray,
				fragCoord.xy,
				float(cascadeIndex),
				momentReceiverDepth,
				ShadowMapEncoding,
				ShadowMomentParams0.x,
				ShadowMomentParams0.y,
				ShadowMomentParams0.z,
				ShadowMomentParams0.w,
				momentMipLevel,
				useMomentMipmaps) * contact;
		}

		return SampleShadowMapArrayFilteredLocal(
			ShadowMapArray,
			fragCoord,
			float(cascadeIndex),
			bias,
			ShadowBlockerSamples,
			ShadowFilterSamples,
			filterRadius,
			ShadowBlockerSearchRadius * cascadeScale,
			SoftShadowMode,
			LightSourceRadius,
			ShadowMinPenumbra * cascadeScale,
			ShadowMaxPenumbra * cascadeScale,
			ShadowVogelTapCount) * contact;
}

vec4 DebugCascadeShadowProbe(in vec3 fragPosWS, in vec3 N, in int cascadeIndex)
{
	if (!LightHasShadowMap)
		return vec4(1.0f, 0.0f, 1.0f, 0.0f);

	ivec4 atlasState = ivec4(0, -1, XRENGINE_SHADOW_FALLBACK_LIT, -1);
	bool atlasSampleAllowed = false;
	if (DirectionalShadowAtlasEnabled)
	{
		atlasState = DirectionalShadowAtlasPacked0[cascadeIndex];
		bool atlasPageValid = atlasState.x != 0 && atlasState.y >= 0 && atlasState.y < textureSize(DirectionalShadowAtlas, 0).z;
		float renderedAge = LightData.RenderedCascadeStaleAge[cascadeIndex];
		bool staleTileFallback = atlasState.z == XRENGINE_SHADOW_FALLBACK_STALE_TILE;
		atlasSampleAllowed = atlasPageValid &&
			renderedAge >= 0.0f &&
			(!staleTileFallback || renderedAge <= DirectionalShadowAtlasMaxStaleFrames);
	}

	mat4 lightMatrix = atlasSampleAllowed ? LightData.RenderedCascadeMatrices[cascadeIndex] : LightData.CascadeMatrices[cascadeIndex];
	float receiverOffset = atlasSampleAllowed ? LightData.RenderedCascadeReceiverOffsets[cascadeIndex] : LightData.CascadeReceiverOffsets[cascadeIndex];
	vec3 currentOffsetPosWS = fragPosWS + N * LightData.CascadeReceiverOffsets[cascadeIndex];
	vec3 currentFragCoord = XRENGINE_ProjectShadowCoord(LightData.CascadeMatrices[cascadeIndex], currentOffsetPosWS);
	vec3 offsetPosWS = fragPosWS + N * receiverOffset;
	vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

	if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
		fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
		fragCoord.z < 0.0f || fragCoord.z > 1.0f)
		return vec4(fragCoord.z, 0.0f, 0.0f, -1.0f);

	float cascadeScale = 1.0f + float(cascadeIndex) * 0.35f;
	float filterRadius = ShadowFilterRadius * cascadeScale;
	float atlasResolutionScale = 1.0f;
	if (atlasSampleAllowed)
	{
		atlasResolutionScale = max(DirectionalShadowAtlasDepthParams[cascadeIndex].w, 1.0f);
	}

	float atlasAuthoredTexelSize = DirectionalShadowAtlasDepthParams[cascadeIndex].z / atlasResolutionScale;
	float constantBias = atlasSampleAllowed ? LightData.RenderedCascadeBiasMin[cascadeIndex] : LightData.CascadeBiasMin[cascadeIndex];
	float maxBias = atlasSampleAllowed ? LightData.RenderedCascadeBiasMax[cascadeIndex] : LightData.CascadeBiasMax[cascadeIndex];
	vec2 shadowTexelSize = atlasSampleAllowed && DirectionalShadowAtlasDepthParams[cascadeIndex].z > 0.0f
		? vec2(max(atlasAuthoredTexelSize, 1e-7f))
		: 1.0f / vec2(textureSize(ShadowMapArray, 0).xy);
	float bias = XRENGINE_ComputeShadowDepthBias(
		fragCoord,
		shadowTexelSize,
		filterRadius,
		constantBias,
		maxBias);

	float sampleDepth = 0.0f;
	vec2 atlasUv = fragCoord.xy;
	if (DirectionalShadowAtlasEnabled)
	{
		ivec4 atlasI0 = atlasState;
		if (!atlasSampleAllowed)
			return vec4(fragCoord.z, 0.0f, 0.0f, -2.0f);

		atlasUv = XRENGINE_ShadowAtlasUvFromLocal(fragCoord.xy, DirectionalShadowAtlasUvScaleBias[cascadeIndex]);
		sampleDepth = texture(DirectionalShadowAtlas, vec3(atlasUv, float(atlasI0.y))).r;
	}
	else
	{
		sampleDepth = texture(ShadowMapArray, vec3(fragCoord.xy, float(cascadeIndex))).r;
	}

	float debugValue = fragCoord.z;
	if (DeferredDebugMode == 11)
		debugValue = fragCoord.x;
	else if (DeferredDebugMode == 12)
		debugValue = fragCoord.y;
	else if (DeferredDebugMode == 13)
		debugValue = atlasUv.x;
	else if (DeferredDebugMode == 14)
		debugValue = atlasUv.y;
	else if (DeferredDebugMode == 15)
		debugValue = currentFragCoord.x;
	else if (DeferredDebugMode == 16)
		debugValue = fragCoord.x;
	else if (DeferredDebugMode == 17)
		debugValue = DirectionalShadowAtlasMaxStaleFrames <= 0.0f
			? 0.0f
			: clamp(LightData.RenderedCascadeStaleAge[cascadeIndex] / DirectionalShadowAtlasMaxStaleFrames, 0.0f, 1.0f);
	else if (DeferredDebugMode == 18)
	{
		float border = min(min(fragCoord.x, 1.0f - fragCoord.x), min(fragCoord.y, 1.0f - fragCoord.y));
		debugValue = atlasSampleAllowed
			? smoothstep(0.0f, max(DirectionalShadowAtlasDepthParams[cascadeIndex].z * 4.0f, 1e-5f), border)
			: 0.0f;
	}

	return vec4(debugValue, sampleDepth, XRENGINE_ShadowLit(fragCoord.z, sampleDepth, bias), 1.0f);
}

vec3 CalcColor(
in float NoL,
in float NoH,
in float NoV,
in float HoV,
in float lightAttenuation,
in vec3 albedo,
in vec3 rms,
in vec3 F0)
{
		float roughness = rms.x;
		float metallic = rms.y;
		float specular = rms.z;
		float D = XRENGINE_D_GGX(NoH, roughness);
		float G = XRENGINE_G_Smith(NoV, NoL, roughness);
		vec3 F = XRENGINE_F_SchlickFast(HoV, F0);
		vec3 spec = specular * XRENGINE_CookTorranceSpecular(D, G, F, NoV, NoL);

		vec3 kD = 1.0f - F;
		kD *= 1.0f - metallic;

		vec3 radiance = lightAttenuation * LightData.Color * LightData.DiffuseIntensity;
		return (kD * albedo / PI + spec) * radiance * NoL;
}
vec3 CalcLight(
in vec3 N,
in vec3 V,
in vec3 fragPosWS,
in vec3 albedo,
in vec3 rms,
in vec3 F0,
in float viewDepth)
{
		vec3 L = -LightData.Direction;
		vec3 H = normalize(V + L);
		float NoL = max(dot(N, L), 0.0f);
		float NoH = max(dot(N, H), 0.0f);
		float NoV = max(dot(N, V), 0.0f);
		float HoV = max(dot(H, V), 0.0f);

		vec3 color = CalcColor(
				NoL, NoH, NoV, HoV,
				1.0f, albedo, rms, F0);

		float shadow = 1.0f;
		int debugCascadeIndex = -1;
		int debugNextCascadeIndex = -1;
		float debugCascadeBlend = 0.0f;

		if (UseCascadedDirectionalShadows && EnableCascadedShadows && LightData.CascadeCount > 0)
		{
			int cascadeCount = min(LightData.CascadeCount, MAX_CASCADES);

			for (int i = 0; i < cascadeCount; ++i)
			{
				float splitFar = LightData.CascadeSplits[i];
				bool isLast = (i == cascadeCount - 1);

				if (viewDepth <= splitFar || isLast)
				{
					if ((DeferredDebugMode >= 7 && DeferredDebugMode <= 9) || (DeferredDebugMode >= 11 && DeferredDebugMode <= 18))
					{
						vec4 probe = DebugCascadeShadowProbe(fragPosWS, N, i);
						if (probe.w < 0.0f)
							return probe.w < -1.5f ? vec3(0.0f, 0.0f, 1.0f) : vec3(1.0f, 0.0f, 1.0f);
						if (probe.w == 0.0f)
							return vec3(1.0f, 0.0f, 0.0f);
						if (DeferredDebugMode == 7)
							return vec3(probe.x);
						if (DeferredDebugMode == 8)
							return vec3(probe.y);
						if (DeferredDebugMode == 9)
							return vec3(probe.z);
						return vec3(probe.x);
					}

					float s0 = ReadCascadeShadowMap(fragPosWS, N, NoL, viewDepth, i);
					if (s0 < 0.0f) s0 = 1.0f;
					debugCascadeIndex = i;

					if (!isLast)
					{
						float blendWidth = LightData.CascadeBlendWidths[i];
						if (blendWidth > 0.0f && viewDepth > splitFar - blendWidth)
						{
							float t = clamp((viewDepth - (splitFar - blendWidth)) / blendWidth, 0.0f, 1.0f);
							float s1 = ReadCascadeShadowMap(fragPosWS, N, NoL, viewDepth, i + 1);
							if (s1 < 0.0f) s1 = s0;
							shadow = mix(s0, s1, t);
							debugNextCascadeIndex = i + 1;
							debugCascadeBlend = t;
							break;
						}
					}

					shadow = s0;
					break;
				}
			}
		}
		else
		{
			shadow = ReadShadowMap2D(fragPosWS, N, NoL, viewDepth, LightData.WorldToLightSpaceMatrix);
		}

		if (DeferredDebugMode == 6)
			return vec3(shadow);

		vec3 result = color * shadow;

		// Debug overlay: tint output with cascade color when enabled
		if (DebugCascadeColors && debugCascadeIndex >= 0)
		{
			vec3 debugColor = CascadeDebugColorTable[debugCascadeIndex % MAX_CASCADES];
			if (debugNextCascadeIndex >= 0)
			{
				vec3 nextDebugColor = CascadeDebugColorTable[debugNextCascadeIndex % MAX_CASCADES];
				debugColor = mix(debugColor, nextDebugColor, debugCascadeBlend);
			}
			result = mix(result, debugColor * max(shadow, 0.15), 0.5);
		}

		return result;
}
vec3 CalcTotalLight(
in vec3 fragPosWS,
in vec3 normal,
in vec3 albedo,
in vec3 rms,
in vec3 cameraPosition,
in float viewDepth)
{
		float metallic = rms.y;
		vec3 V = normalize(cameraPosition - fragPosWS);
		vec3 F0 = mix(vec3(0.04f), albedo, metallic);
		return CalcLight(normal, V, fragPosWS, albedo, rms, F0, viewDepth);
}
void main()
{
		vec2 fragCoordLocal = XRENGINE_FramebufferCoordLocal(gl_FragCoord.xy, ScreenOrigin);
		vec2 uv = clamp(fragCoordLocal / vec2(ScreenWidth, ScreenHeight), vec2(0.0f), vec2(1.0f));
#ifdef XRENGINE_MSAA_DEFERRED
		ivec2 coord = clamp(ivec2(floor(fragCoordLocal)), ivec2(0), textureSize(DepthView) - ivec2(1));
		vec3 albedo = texelFetch(AlbedoOpacity, coord, gl_SampleID).rgb;
		vec3 normal = XRENGINE_ReadNormalMS(Normal, coord, gl_SampleID);
		vec3 rms = texelFetch(RMSE, coord, gl_SampleID).rgb;
		float depth = texelFetch(DepthView, coord, gl_SampleID).r;
#elif defined(XRENGINE_STEREO_DEFERRED)
		vec3 uvi = vec3(uv, gl_ViewID_OVR);
		vec3 albedo = texture(AlbedoOpacity, uvi).rgb;
		vec3 normal = XRENGINE_ReadNormal(Normal, uvi);
		vec3 rms = texture(RMSE, uvi).rgb;
		float depth = texture(DepthView, uvi).r;
#else
		//Retrieve shading information from GBuffer textures
		vec3 albedo = texture(AlbedoOpacity, uv).rgb;
		vec3 normal = XRENGINE_ReadNormal(Normal, uv);
		vec3 rms = texture(RMSE, uv).rgb;
		float depth = texture(DepthView, uv).r;
#endif
		if (XRENGINE_ResolveDepth(depth) >= 1.0f)
		{
			OutColor = vec3(0.0f);
			return;
		}

		// Drive cascade selection from the G-buffer's own player-camera depth.
		// Reusing this view-space reconstruction keeps CSM blend boundaries tied
		// to the rendered camera even when world reconstruction changes.
#ifdef XRENGINE_STEREO_DEFERRED
		bool leftEye = gl_ViewID_OVR == 0;
		mat4 inverseProjMatrix = leftEye ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
		mat4 inverseViewMatrix = leftEye ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
		vec3 fragPosVS = XRENGINE_ViewPosFromDepthRaw(depth, uv, inverseProjMatrix);
		float viewDepth = abs(fragPosVS.z);
		vec3 fragPosWS = (inverseViewMatrix * vec4(fragPosVS, 1.0f)).xyz;
		vec3 cameraPosition = inverseViewMatrix[3].xyz;
#else
		vec3 fragPosVS = XRENGINE_ViewPosFromDepthRaw(depth, uv, InverseProjMatrix);
		float viewDepth = abs(fragPosVS.z);
		vec3 fragPosWS = (InverseViewMatrix * vec4(fragPosVS, 1.0f)).xyz;
		vec3 cameraPosition = InverseViewMatrix[3].xyz;
#endif

		OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms, cameraPosition, viewDepth);
}
