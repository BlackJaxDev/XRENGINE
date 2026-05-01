#version 450

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

layout(location = 0) out vec3 OutColor; //Diffuse lighting output
layout(location = 0) in vec3 FragPos;

#ifdef XRENGINE_MSAA_DEFERRED
layout(binding = 0) uniform sampler2DMS AlbedoOpacity;
layout(binding = 1) uniform sampler2DMS Normal;
layout(binding = 2) uniform sampler2DMS RMSE;
layout(binding = 3) uniform sampler2DMS DepthView;
#else
layout(binding = 0) uniform sampler2D AlbedoOpacity; //AlbedoOpacity
layout(binding = 1) uniform sampler2D Normal; //Normal
layout(binding = 2) uniform sampler2D RMSE; //PBR: Roughness, Metallic, Specular, Index of refraction
layout(binding = 3) uniform sampler2D DepthView; //Depth
#endif
uniform sampler2D ShadowMap; //Directional Shadow Map
uniform sampler2DArray ShadowMapArray; //Directional Cascaded Shadow Map
layout(binding = 30) uniform sampler2D DirectionalShadowAtlasPages[2];
uniform bool UseCascadedDirectionalShadows = false;
uniform bool LightHasShadowMap = true;
uniform bool EnableCascadedShadows = true;
uniform bool DebugCascadeColors = false;
uniform bool DirectionalShadowAtlasEnabled = false;
uniform ivec4 DirectionalShadowAtlasPacked0[MAX_CASCADES]; // enabled, page, fallback, record index
uniform vec4 DirectionalShadowAtlasUvScaleBias[MAX_CASCADES];
uniform vec4 DirectionalShadowAtlasDepthParams[MAX_CASCADES]; // near, far, local texel size, reserved

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
		lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0f : 0.0f;
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
			lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0f : 0.0f;
		}
	}

	return lit / sampleCount;
}

float SampleShadowMapSimpleLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias)
{
	float depth = texture(shadowMap, shadowCoord.xy).r;
	return (shadowCoord.z - bias) <= depth ? 1.0f : 0.0f;
}

float SampleShadowMapTent4Local(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in float filterRadius)
{
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
	vec2 radius = max(vec2(max(filterRadius, 0.0f)), texelSize);
	float lit = 0.0f;

	lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5f, -0.5f) * radius).r ? 1.0f : 0.0f;
	lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(0.5f, -0.5f) * radius).r ? 1.0f : 0.0f;
	lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5f, 0.5f) * radius).r ? 1.0f : 0.0f;
	lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(0.5f, 0.5f) * radius).r ? 1.0f : 0.0f;

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
		lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0f : 0.0f;
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
			lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0f : 0.0f;
		}
	}

	return lit / sampleCount;
}

float SampleShadowMapArraySimpleLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias)
{
	float depth = texture(shadowMap, vec3(shadowCoord.xy, layer)).r;
	return (shadowCoord.z - bias) <= depth ? 1.0f : 0.0f;
}

float SampleShadowMapArrayTent4Local(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in float filterRadius)
{
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
	vec2 radius = max(vec2(max(filterRadius, 0.0f)), texelSize);
	float lit = 0.0f;

	lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f, -0.5f) * radius, layer)).r ? 1.0f : 0.0f;
	lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5f, -0.5f) * radius, layer)).r ? 1.0f : 0.0f;
	lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5f, 0.5f) * radius, layer)).r ? 1.0f : 0.0f;
	lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5f, 0.5f) * radius, layer)).r ? 1.0f : 0.0f;

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
		if (d < receiverDepth) { blockerSum += d; blockerCount++; }
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
		if (d < receiverDepth) { blockerSum += d; blockerCount++; }
	}
	return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0f;
}

float SampleShadowMapCHSSLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in int sampleCount, in float searchRadius, in float lightSourceRadius)
{
	float receiverDepth = shadowCoord.z - bias;
	float avgBlocker = BlockerSearch2DLocal(shadowMap, shadowCoord.xy, receiverDepth, searchRadius, sampleCount);
	if (avgBlocker < 0.0f) return 1.0f;
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0));
	float minR = max(texelSize.x, texelSize.y);
	float penumbra = clamp((receiverDepth - avgBlocker) / max(avgBlocker, 0.0001f) * lightSourceRadius, minR, searchRadius * 4.0f);
	return SampleShadowMapSoftLocal(shadowMap, shadowCoord, bias, sampleCount, penumbra);
}

float SampleShadowMapArrayCHSSLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in int sampleCount, in float searchRadius, in float lightSourceRadius)
{
	float receiverDepth = shadowCoord.z - bias;
	float avgBlocker = BlockerSearch2DArrayLocal(shadowMap, shadowCoord.xy, layer, receiverDepth, searchRadius, sampleCount);
	if (avgBlocker < 0.0f) return 1.0f;
	vec2 texelSize = 1.0f / vec2(textureSize(shadowMap, 0).xy);
	float minR = max(texelSize.x, texelSize.y);
	float penumbra = clamp((receiverDepth - avgBlocker) / max(avgBlocker, 0.0001f) * lightSourceRadius, minR, searchRadius * 4.0f);
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

float SampleDeferredContactShadow(in vec3 fragPosWS, in vec3 N, in vec3 lightDirWS, in float receiverOffset, in float compareBias, in float viewDepth)
{
	int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
		ContactShadowSamples,
		viewDepth,
		ContactShadowDistance);
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
		receiverOffset,
		compareBias,
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
		receiverOffset,
		compareBias,
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
	in vec3 atlasCoord,
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

		float receiverOffset = max(ShadowBiasProjectionParams.y, 0.0f);
		vec3 offsetPosWS = fragPosWS + N * receiverOffset;
		vec4 fragPosLightSpace = lightMatrix * vec4(offsetPosWS, 1.0f);
		vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
		fragCoord = fragCoord * 0.5f + 0.5f;

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
			fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
			fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return 1.0f;

		vec2 shadowTexelSize = 1.0f / vec2(textureSize(ShadowMap, 0));
		float bias = XRENGINE_ComputeShadowDepthBias(
			fragCoord,
			shadowTexelSize,
			ShadowFilterRadius,
			ShadowBiasProjectionParams.x,
			max(ShadowBiasParams.y, 0.0f));
		float contact = EnableContactShadows
			? SampleDeferredContactShadow(fragPosWS, N, normalize(-LightData.Direction), receiverOffset, bias, viewDepth)
			: 1.0f;

		if (DirectionalShadowAtlasEnabled)
		{
			ivec4 atlasI0 = DirectionalShadowAtlasPacked0[0];
			bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < 2;
			int fallbackMode = atlasI0.z;
			if (atlasEnabled)
			{
				vec4 atlasUvScaleBias = DirectionalShadowAtlasUvScaleBias[0];
				vec2 atlasUv = fragCoord.xy * atlasUvScaleBias.xy + atlasUvScaleBias.zw;
				float atlasRadiusScale = max(atlasUvScaleBias.x, atlasUvScaleBias.y);
				vec2 atlasTexelSize = max(vec2(DirectionalShadowAtlasDepthParams[0].z) * atlasUvScaleBias.xy, vec2(1e-7f));
				float atlasBias = XRENGINE_ComputeShadowDepthBias(
					vec3(atlasUv, fragCoord.z),
					atlasTexelSize,
					ShadowFilterRadius * atlasRadiusScale,
					ShadowBiasProjectionParams.x,
					max(ShadowBiasParams.y, 0.0f));
				return SampleDirectionalAtlasPage(
					atlasI0.y,
					vec3(atlasUv, fragCoord.z),
					atlasBias,
					ShadowBlockerSamples,
					ShadowFilterSamples,
					ShadowFilterRadius * atlasRadiusScale,
					ShadowBlockerSearchRadius * atlasRadiusScale,
					SoftShadowMode,
					LightSourceRadius * atlasRadiusScale,
					ShadowMinPenumbra * atlasRadiusScale,
					ShadowMaxPenumbra * atlasRadiusScale,
					ShadowVogelTapCount) * contact;
			}

			if (fallbackMode == 1 || fallbackMode == 2 || fallbackMode == 4)
				return contact;
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
	in vec3 atlasCoord,
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
	if (pageIndex == 0)
	{
		return XRENGINE_SampleShadowMapFiltered(
			DirectionalShadowAtlasPages[0],
			atlasCoord,
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

	if (pageIndex == 1)
	{
		return XRENGINE_SampleShadowMapFiltered(
			DirectionalShadowAtlasPages[1],
			atlasCoord,
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

	return 1.0f;
}

float ReadDirectionalAtlasCenterDepth(in int pageIndex, in vec2 uv)
{
	if (pageIndex == 0)
		return texture(DirectionalShadowAtlasPages[0], uv).r;
	if (pageIndex == 1)
		return texture(DirectionalShadowAtlasPages[1], uv).r;
	return 1.0f;
}

float ReadCascadeShadowMap(in vec3 fragPosWS, in vec3 N, in float NoL, in float viewDepth, in int cascadeIndex)
{
		if (!LightHasShadowMap)
			return 1.0f;

		mat4 lightMatrix = LightData.CascadeMatrices[cascadeIndex];
		float receiverOffset = LightData.CascadeReceiverOffsets[cascadeIndex];
		vec3 offsetPosWS = fragPosWS + N * receiverOffset;
		vec4 fragPosLightSpace = lightMatrix * vec4(offsetPosWS, 1.0f);
		vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
		fragCoord = fragCoord * 0.5f + 0.5f;

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
			fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
			fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return -1.0f;

		float cascadeScale = 1.0f + float(cascadeIndex) * 0.35f;
		float filterRadius = ShadowFilterRadius * cascadeScale;
		vec2 shadowTexelSize = 1.0f / vec2(textureSize(ShadowMapArray, 0).xy);
		float bias = XRENGINE_ComputeShadowDepthBias(
			fragCoord,
			shadowTexelSize,
			filterRadius,
			LightData.CascadeBiasMin[cascadeIndex],
			LightData.CascadeBiasMax[cascadeIndex]);
		float contact = EnableContactShadows
			? SampleDeferredContactShadow(fragPosWS, N, normalize(-LightData.Direction), receiverOffset, bias, viewDepth)
			: 1.0f;

		if (DirectionalShadowAtlasEnabled)
		{
			ivec4 atlasI0 = DirectionalShadowAtlasPacked0[cascadeIndex];
			bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < 2;
			int fallbackMode = atlasI0.z;
			if (atlasEnabled)
			{
				vec4 atlasUvScaleBias = DirectionalShadowAtlasUvScaleBias[cascadeIndex];
				vec2 atlasUv = fragCoord.xy * atlasUvScaleBias.xy + atlasUvScaleBias.zw;
				float atlasRadiusScale = max(atlasUvScaleBias.x, atlasUvScaleBias.y);
				vec2 atlasTexelSize = max(vec2(DirectionalShadowAtlasDepthParams[cascadeIndex].z) * atlasUvScaleBias.xy, vec2(1e-7f));
				float atlasBias = XRENGINE_ComputeShadowDepthBias(
					vec3(atlasUv, fragCoord.z),
					atlasTexelSize,
					filterRadius * atlasRadiusScale,
					LightData.CascadeBiasMin[cascadeIndex],
					LightData.CascadeBiasMax[cascadeIndex]);
				return SampleDirectionalAtlasPage(
					atlasI0.y,
					vec3(atlasUv, fragCoord.z),
					atlasBias,
					ShadowBlockerSamples,
					ShadowFilterSamples,
					filterRadius * atlasRadiusScale,
					ShadowBlockerSearchRadius * cascadeScale * atlasRadiusScale,
					SoftShadowMode,
					LightSourceRadius * atlasRadiusScale,
					ShadowMinPenumbra * cascadeScale * atlasRadiusScale,
					ShadowMaxPenumbra * cascadeScale * atlasRadiusScale,
					ShadowVogelTapCount) * contact;
			}

			if (fallbackMode == 2)
				return contact;
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
		vec2 fragCoordLocal = gl_FragCoord.xy - ScreenOrigin;
		vec2 uv = clamp(fragCoordLocal / vec2(ScreenWidth, ScreenHeight), vec2(0.0f), vec2(1.0f));
#ifdef XRENGINE_MSAA_DEFERRED
		ivec2 coord = ivec2(floor(fragCoordLocal));
		vec3 albedo = texelFetch(AlbedoOpacity, coord, gl_SampleID).rgb;
		vec3 normal = XRENGINE_ReadNormalMS(Normal, coord, gl_SampleID);
		vec3 rms = texelFetch(RMSE, coord, gl_SampleID).rgb;
		float depth = texelFetch(DepthView, coord, gl_SampleID).r;
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
		vec3 fragPosVS = XRENGINE_ViewPosFromDepthRaw(depth, uv, InverseProjMatrix);
		float viewDepth = abs(fragPosVS.z);
		vec3 fragPosWS = (InverseViewMatrix * vec4(fragPosVS, 1.0f)).xyz;
		vec3 cameraPosition = InverseViewMatrix[3].xyz;

		OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms, cameraPosition, viewDepth);
}
