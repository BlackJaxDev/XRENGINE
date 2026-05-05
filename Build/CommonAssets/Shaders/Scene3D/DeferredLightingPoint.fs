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
uniform samplerCube ShadowMap; //Point Shadow Map
uniform sampler2DArray PointShadowAtlas;

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 ViewProjectionMatrix;
uniform float ShadowNearPlaneDist = 0.1f;
uniform float ShadowBase = 0.035f;
uniform float ShadowMult = 1.221f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;
uniform vec4 ShadowBiasParams = vec4(1.0f, 2.0f, 1.0f, 0.0f); // depth texels, slope texels, normal texels, reserved
uniform bool LightHasShadowMap = true; // Added
uniform bool PointShadowAtlasPathEnabled = false;
uniform ivec4 PointShadowAtlasPacked0[6]; // enabled, page, fallback, record index
uniform vec4 PointShadowAtlasUvScaleBias[6];
uniform vec4 PointShadowAtlasDepthParams[6]; // near, far, local texel size, requested/allocated scale
uniform int ShadowSamples = 4;
uniform int ShadowBlockerSamples = 4;
uniform int ShadowFilterSamples = 4;
uniform int ShadowVogelTapCount = 5;
uniform float ShadowFilterRadius = 0.0012f;
uniform float ShadowBlockerSearchRadius = 0.0012f;
uniform float ShadowMinPenumbra = 0.0002f;
uniform float ShadowMaxPenumbra = 0.0048f;
uniform int SoftShadowMode = 1;
uniform float LightSourceRadius = 0.01f;
uniform bool EnableContactShadows = true;
uniform float ContactShadowDistance = 0.1f;
uniform int ContactShadowSamples = 4;
uniform float ContactShadowThickness = 0.25f;
uniform float ContactShadowFadeStart = 10.0f;
uniform float ContactShadowFadeEnd = 40.0f;
uniform float ContactShadowNormalOffset = 0.0f;
uniform float ContactShadowJitterStrength = 1.0f;
// Debug: 0=normal, 1=shadow-only (white=lit), 2=margin heatmap (green=lit, red=shadow)
uniform int ShadowDebugMode = 0;

const int XRENGINE_POINT_SHADOW_FACE_COUNT = 6;
const int XRENGINE_SHADOW_FALLBACK_LIT = 1;
const int XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY = 2;
const int XRENGINE_SHADOW_FALLBACK_LEGACY = 5;

struct PointLight
{
    vec3 Color;
    float DiffuseIntensity;
    vec3 Position;
    float Radius;
    float Brightness;
};
uniform PointLight LightData;

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

const vec3 LocalShadowCubeKernel[20] = vec3[](
	vec3( 1.0,  1.0,  1.0), vec3( 1.0, -1.0,  1.0),
	vec3(-1.0,  1.0,  1.0), vec3(-1.0, -1.0,  1.0),
	vec3( 1.0,  1.0, -1.0), vec3( 1.0, -1.0, -1.0),
	vec3(-1.0,  1.0, -1.0), vec3(-1.0, -1.0, -1.0),
	vec3( 1.0,  0.0,  0.0), vec3(-1.0,  0.0,  0.0),
	vec3( 0.0,  1.0,  0.0), vec3( 0.0, -1.0,  0.0),
	vec3( 0.0,  0.0,  1.0), vec3( 0.0,  0.0, -1.0),
	vec3( 1.0,  1.0,  0.0), vec3( 1.0, -1.0,  0.0),
	vec3(-1.0,  1.0,  0.0), vec3(-1.0, -1.0,  0.0),
	vec3( 0.0,  1.0,  1.0), vec3( 0.0, -1.0, -1.0)
);

const int LocalShadowCubeKernelTapOrder[20] = int[](
	0, 3, 5, 6,
	7, 4, 2, 1,
	8, 9, 10, 11,
	12, 13, 14, 17,
	15, 16, 18, 19
);

vec3 GetShadowCubeKernelTapLocal(int tapIndex)
{
	return LocalShadowCubeKernel[LocalShadowCubeKernelTapOrder[tapIndex]];
}

float SampleShadowCubePCFLocal(in samplerCube shadowMap, in vec3 shadowDir, in float biasedLightDist, in float farPlaneDist, in float sampleRadius)
{
	float lit = 0.0f;

	for (int i = 0; i < 20; ++i)
	{
		vec3 sampleDir = normalize(shadowDir + GetShadowCubeKernelTapLocal(i) * sampleRadius);
		float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
		lit += biasedLightDist <= sampleDepth ? 1.0f : 0.0f;
	}

	return lit / 20.0f;
}

float GetShadowBias(in float NoL)
{
    float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}

float GetShadowCubeSampleRadius(in samplerCube shadowMap, in float filterRadius)
{
	float faceSize = max(float(textureSize(shadowMap, 0).x), 1.0f);
	float texelDirectionSpan = 2.0f / faceSize;
	float requestedScale = clamp(filterRadius * 256.0f, 0.0f, 4.0f);
	return texelDirectionSpan * max(1.0f, requestedScale);
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

float BlockerSearchCubeLocal(in samplerCube shadowMap, in vec3 shadowDir, in float receiverDepth, in float farPlaneDist, in float sampleRadius, in int sampleCount)
{
	float blockerSum = 0.0f;
	int blockerCount = 0;
	int clampedSamples = clamp(sampleCount, 1, 20);
	for (int i = 0; i < 20; ++i)
	{
		if (i >= clampedSamples) break;
		vec3 sampleDir = normalize(shadowDir + GetShadowCubeKernelTapLocal(i) * sampleRadius);
		float d = texture(shadowMap, sampleDir).r * farPlaneDist;
		if (d < receiverDepth) { blockerSum += d; blockerCount++; }
	}
	return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0f;
}

float SampleShadowCubeFilteredLocal(
	in samplerCube shadowMap,
	in vec3 shadowDir,
	in float biasedLightDist,
	in float farPlaneDist,
	in float sampleRadius,
	in float blockerSearchRadius,
	in int blockerSamples,
	in int filterSamples,
	in int softMode,
	in float lightSourceRadius,
	in float minPenumbra,
	in float maxPenumbra,
	in int vogelTapCount)
{
	return XRENGINE_SampleShadowCubeFiltered(
		shadowMap,
		shadowDir,
		biasedLightDist,
		0.0f,
		farPlaneDist,
		sampleRadius,
		blockerSearchRadius,
		blockerSamples,
		filterSamples,
		softMode,
		lightSourceRadius,
		minPenumbra,
		maxPenumbra,
		vogelTapCount);
}

int SelectPointShadowAtlasFace(vec3 lightToReceiver, out vec2 localUv)
{
	vec3 a = abs(lightToReceiver);
	if (a.x >= a.y && a.x >= a.z)
	{
		if (lightToReceiver.x >= 0.0f)
		{
			localUv = vec2(-lightToReceiver.z, -lightToReceiver.y) / max(a.x, 1e-6f);
			localUv = localUv * 0.5f + vec2(0.5f);
			return 0;
		}

		localUv = vec2(lightToReceiver.z, -lightToReceiver.y) / max(a.x, 1e-6f);
		localUv = localUv * 0.5f + vec2(0.5f);
		return 1;
	}

	if (a.y >= a.z)
	{
		if (lightToReceiver.y >= 0.0f)
		{
			localUv = vec2(lightToReceiver.x, lightToReceiver.z) / max(a.y, 1e-6f);
			localUv = localUv * 0.5f + vec2(0.5f);
			return 2;
		}

		localUv = vec2(lightToReceiver.x, -lightToReceiver.z) / max(a.y, 1e-6f);
		localUv = localUv * 0.5f + vec2(0.5f);
		return 3;
	}

	if (lightToReceiver.z >= 0.0f)
	{
		localUv = vec2(lightToReceiver.x, -lightToReceiver.y) / max(a.z, 1e-6f);
		localUv = localUv * 0.5f + vec2(0.5f);
		return 4;
	}

	localUv = vec2(-lightToReceiver.x, -lightToReceiver.y) / max(a.z, 1e-6f);
	localUv = localUv * 0.5f + vec2(0.5f);
	return 5;
}

float SamplePointAtlasPage(
	int pageIndex,
	vec3 localCoord,
	vec4 uvScaleBias,
	float localTexelSize,
	float bias)
{
	if (pageIndex < 0 || pageIndex >= textureSize(PointShadowAtlas, 0).z)
		return 1.0f;

	return XRENGINE_SampleShadowAtlasFiltered(
		PointShadowAtlas,
		localCoord,
		float(pageIndex),
		uvScaleBias,
		localTexelSize,
		bias,
		ShadowBlockerSamples,
		ShadowFilterSamples,
		ShadowFilterRadius,
		ShadowBlockerSearchRadius,
		SoftShadowMode,
		LightSourceRadius,
		ShadowMinPenumbra,
		ShadowMaxPenumbra,
		ShadowVogelTapCount);
}

float ReadPointAtlasCenterDepth(int pageIndex, vec2 localUv, vec4 uvScaleBias)
{
	if (pageIndex >= 0 && pageIndex < textureSize(PointShadowAtlas, 0).z)
		return XRENGINE_ReadShadowAtlasDepth(PointShadowAtlas, localUv, float(pageIndex), uvScaleBias);
	return 1.0f;
}

float GetPointAtlasSampleRadius(float authoredTexelSize, float filterRadius)
{
	float requestedScale = clamp(filterRadius * 256.0f, 0.0f, 4.0f);
	return 2.0f * max(authoredTexelSize, 1e-7f) * max(1.0f, requestedScale);
}

float ReadPointAtlasDepthForDirection(vec3 sampleDirection, out bool sampleable)
{
	vec2 localUv;
	int faceIndex = SelectPointShadowAtlasFace(sampleDirection, localUv);
	if (faceIndex < 0 || faceIndex >= XRENGINE_POINT_SHADOW_FACE_COUNT)
	{
		sampleable = false;
		return 1.0f;
	}

	ivec4 atlasPacked0 = PointShadowAtlasPacked0[faceIndex];
	sampleable = atlasPacked0.x != 0 &&
		atlasPacked0.y >= 0 &&
		atlasPacked0.y < textureSize(PointShadowAtlas, 0).z;
	if (!sampleable)
		return 1.0f;

	return XRENGINE_ReadShadowAtlasDepth(
		PointShadowAtlas,
		localUv,
		float(atlasPacked0.y),
		PointShadowAtlasUvScaleBias[faceIndex]);
}

float SamplePointAtlasDirection(vec3 sampleDirection, float receiverDepth, float bias)
{
	bool sampleable;
	float sampleDepth = ReadPointAtlasDepthForDirection(sampleDirection, sampleable);
	if (!sampleable)
		return 1.0f;

	return (receiverDepth - bias) <= sampleDepth ? 1.0f : 0.0f;
}

float SamplePointAtlasCubeSimple(vec3 shadowDir, float receiverDepth, float bias)
{
	return SamplePointAtlasDirection(normalize(shadowDir), receiverDepth, bias);
}

float SamplePointAtlasCubePCF(vec3 shadowDir, float receiverDepth, float bias, float sampleRadius)
{
	float lit = 0.0f;
	vec3 baseDir = normalize(shadowDir);
	float radius = max(sampleRadius, 0.000001f);

	for (int i = 0; i < 20; ++i)
	{
		vec3 sampleDir = normalize(baseDir + GetShadowCubeKernelTapLocal(i) * radius);
		lit += SamplePointAtlasDirection(sampleDir, receiverDepth, bias);
	}

	return lit / 20.0f;
}

float SamplePointAtlasCubeVogel(vec3 shadowDir, float receiverDepth, float bias, float sampleRadius, int tapCount)
{
	int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps);
	if (clampedTaps <= 1)
		return SamplePointAtlasCubeSimple(shadowDir, receiverDepth, bias);

	vec3 baseDir = normalize(shadowDir);
	vec3 tangent;
	vec3 bitangent;
	XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent);
	float radius = max(sampleRadius, 0.000001f);
	float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0f;
	float lit = 0.0f;

	for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
	{
		if (i >= clampedTaps)
			break;

		vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * radius;
		vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y);
		lit += SamplePointAtlasDirection(sampleDir, receiverDepth, bias);
	}

	return lit / float(clampedTaps);
}

float BlockerSearchPointAtlasCube(vec3 shadowDir, float receiverDepth, float sampleRadius, int sampleCount)
{
	float blockerSum = 0.0f;
	int blockerCount = 0;
	int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps);
	vec3 baseDir = normalize(shadowDir);
	vec3 tangent;
	vec3 bitangent;
	XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent);
	float radius = max(sampleRadius, 0.000001f);
	float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0f;

	for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
	{
		if (i >= clampedSamples)
			break;

		vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius;
		vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y);
		bool sampleable;
		float sampleDepth = ReadPointAtlasDepthForDirection(sampleDir, sampleable);
		if (sampleable && sampleDepth < receiverDepth)
		{
			blockerSum += sampleDepth;
			blockerCount++;
		}
	}

	return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0f;
}

float SamplePointAtlasCubeCHSS(
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
	float avgBlockerDepth = BlockerSearchPointAtlasCube(shadowDir, biasedReceiverDepth, blockerSearchRadius, blockerSamples);
	if (avgBlockerDepth < 0.0f)
		return 1.0f;

	float receiverWorldDepth = biasedReceiverDepth * farPlaneDist;
	float blockerWorldDepth = avgBlockerDepth * farPlaneDist;
	float angularSourceRadius = max(lightSourceRadius, 0.0f) / max(blockerWorldDepth, 0.0001f);
	float rawPenumbra = (receiverWorldDepth - blockerWorldDepth) / max(blockerWorldDepth, 0.0001f) * angularSourceRadius;
	float penumbra = XRENGINE_ClampPenumbra(rawPenumbra, max(minPenumbra, 0.000001f), max(maxPenumbra, minPenumbra));

	return SamplePointAtlasCubeVogel(shadowDir, receiverDepth, bias, penumbra, filterSamples);
}

float SamplePointAtlasCubeFiltered(
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
		return SamplePointAtlasCubeVogel(shadowDir, receiverDepth, bias, sampleRadius, vogelTapCount);

	int clampedFilterSamples = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps);
	if (clampedFilterSamples <= 1)
		return SamplePointAtlasCubeSimple(shadowDir, receiverDepth, bias);

	if (softShadowMode == 2)
		return SamplePointAtlasCubeCHSS(
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
		return SamplePointAtlasCubeVogel(shadowDir, receiverDepth, bias, sampleRadius, clampedFilterSamples);

	return SamplePointAtlasCubePCF(shadowDir, receiverDepth, bias, sampleRadius);
}

// Global debug state written by ReadPointShadowMap for visualization
float _dbgShadowLit = 1.0f;
float _dbgShadowMargin = 1.0f;

float ReadPointShadowAtlasMap(in vec3 fragPosWS, in vec3 N, in float NoL)
{
	vec3 lightToFragBase = fragPosWS - LightData.Position;
	float receiverDist = length(lightToFragBase);
	vec2 faceUv;
	int faceIndex = SelectPointShadowAtlasFace(lightToFragBase, faceUv);
	ivec4 atlasPacked0 = PointShadowAtlasPacked0[faceIndex];
	vec4 atlasUvScaleBias = PointShadowAtlasUvScaleBias[faceIndex];
	vec4 atlasDepthParams = PointShadowAtlasDepthParams[faceIndex];

	float nearPlaneDist = max(atlasDepthParams.x, 0.0f);
	float farPlaneDist = max(atlasDepthParams.y, nearPlaneDist + 0.001f);
	float localTexelSize = max(atlasDepthParams.z, 1e-7f);
	float atlasResolutionScale = max(atlasDepthParams.w, 1.0f);
	float authoredTexelSize = max(localTexelSize / atlasResolutionScale, 1e-7f);
	float normalOffset = receiverDist * 2.0f * authoredTexelSize * max(ShadowBiasParams.z, 0.0f);
	vec3 offsetPosWS = fragPosWS + N * normalOffset;
	vec3 fragToLight = offsetPosWS - LightData.Position;
	float lightDist = length(fragToLight);

	if (lightDist >= farPlaneDist) return 1.0f;
	if (lightDist <= nearPlaneDist + normalOffset)
	{
		_dbgShadowMargin = nearPlaneDist / max(farPlaneDist, 0.001f);
		_dbgShadowLit = 1.0f;
		return 1.0f;
	}

	float NoLSafe = max(NoL, 0.05f);
	float tanTheta = sqrt(max(1.0f - NoLSafe * NoLSafe, 0.0f)) / NoLSafe;
	float filterTexels = max(1.0f, clamp(ShadowFilterRadius * 256.0f, 0.0f, 8.0f));
	float texelRel = 2.0f * authoredTexelSize * (
		max(ShadowBiasParams.x, 0.0f) +
		max(ShadowBiasParams.y, 0.0f) * tanTheta * filterTexels);

	float projA = ProjMatrix[2][2];
	float projB = ProjMatrix[3][2];
	float cameraNear = abs(projB / (projA - 1.0f));
	float cameraFar  = abs(projB / (projA + 1.0f));
	vec3  cameraPos   = vec3(InverseViewMatrix[3]);
	float cameraDist  = length(fragPosWS - cameraPos);
	float depthError = 2.0f * cameraDist * cameraDist * abs(cameraFar - cameraNear)
	                 / max(cameraNear * cameraFar * 16777216.0f, 1e-10f);
	float depthRel = depthError / max(lightDist, 0.001f);
	float r16fRel = 1.0f / 512.0f;
	float relThreshold = max(texelRel, max(depthRel, r16fRel));
	float compareBias = lightDist * relThreshold;
	float normalizedBias = compareBias / max(farPlaneDist, 0.001f);
	float viewDepth = length(fragPosWS - cameraPos);
	float contact = EnableContactShadows
		? SampleDeferredContactShadow(fragPosWS, N, normalize(LightData.Position - fragPosWS), normalOffset, compareBias, viewDepth)
		: 1.0f;

	if (atlasPacked0.x == 0)
	{
		float fallbackLit = atlasPacked0.z == XRENGINE_SHADOW_FALLBACK_CONTACT_ONLY ? contact : 1.0f;
		_dbgShadowLit = fallbackLit;
		_dbgShadowMargin = 1.0f;
		return fallbackLit;
	}

	vec2 sampledFaceUv;
	SelectPointShadowAtlasFace(fragToLight, sampledFaceUv);
	float receiverDepth = clamp(lightDist / max(farPlaneDist, 0.001f), 0.0f, 1.0f);
	float sampleRadius = GetPointAtlasSampleRadius(authoredTexelSize, ShadowFilterRadius);
	float blockerSearchRadius = GetPointAtlasSampleRadius(authoredTexelSize, ShadowBlockerSearchRadius);
	float minPenumbra = GetPointAtlasSampleRadius(authoredTexelSize, ShadowMinPenumbra);
	float maxPenumbra = GetPointAtlasSampleRadius(authoredTexelSize, ShadowMaxPenumbra);
	float lit = SamplePointAtlasCubeFiltered(
		fragToLight,
		receiverDepth,
		normalizedBias,
		farPlaneDist,
		sampleRadius,
		blockerSearchRadius,
		ShadowBlockerSamples,
		ShadowFilterSamples,
		SoftShadowMode,
		LightSourceRadius,
		minPenumbra,
		maxPenumbra,
		ShadowVogelTapCount) * contact;

	if (ShadowDebugMode != 0)
	{
		float centerDepth = ReadPointAtlasCenterDepth(atlasPacked0.y, sampledFaceUv, atlasUvScaleBias);
		_dbgShadowMargin = centerDepth - (receiverDepth - normalizedBias);
	}
	_dbgShadowLit = lit;

	return lit;
}

// returns 1 lit, 0 shadow
float ReadPointShadowMap(in float farPlaneDist, in vec3 fragPosWS, in vec3 N, in float NoL)
{
	if (!LightHasShadowMap) return 1.0f;
	if (PointShadowAtlasPathEnabled)
		return ReadPointShadowAtlasMap(fragPosWS, N, NoL);

	vec3 lightToFragBase = fragPosWS - LightData.Position;
	float receiverDist = length(lightToFragBase);
	float faceSize = max(float(textureSize(ShadowMap, 0).x), 1.0f);
	float texelRelBase = 2.0f / faceSize;
	float normalOffset = receiverDist * texelRelBase * max(ShadowBiasParams.z, 0.0f);
	vec3 offsetPosWS = fragPosWS + N * normalOffset;
	vec3 fragToLight = offsetPosWS - LightData.Position;
	float lightDist = length(fragToLight);
	float nearPlaneDist = max(ShadowNearPlaneDist, 0.0f);

	// Beyond the shadow far plane: treat as lit (attenuation handles falloff)
	if (lightDist >= farPlaneDist) return 1.0f;
	// The cubemap shadow cameras clip everything inside the near plane.
	// Triangles crossing that clip can form a synthetic blocker shell around the light,
	// so receivers inside the blind zone must be treated as unshadowed.
	if (lightDist <= nearPlaneDist + normalOffset)
	{
		_dbgShadowMargin = nearPlaneDist / max(farPlaneDist, 0.001f);
		_dbgShadowLit = 1.0f;
		return 1.0f;
	}

	float NoLSafe = max(NoL, 0.05f);
	float tanTheta = sqrt(max(1.0f - NoLSafe * NoLSafe, 0.0f)) / NoLSafe;
	float filterTexels = max(1.0f, clamp(ShadowFilterRadius * 256.0f, 0.0f, 8.0f));
	float texelRel = texelRelBase * (max(ShadowBiasParams.x, 0.0f) + max(ShadowBiasParams.y, 0.0f) * tanTheta * filterTexels);

	// ---- GBuffer depth-precision bias ----
	// The deferred receiver position is reconstructed from a 24-bit depth buffer
	// with near/far = 0.1/10000. World-space reconstruction error at camera
	// distance d is approximately: d^2 * (far - near) / (near * far * 2^24).
	// This quadratic growth makes the error dominant at moderate distances.
	// Extract near/far from the projection matrix so the bias is always correct.
	float projA = ProjMatrix[2][2]; // -(f+n)/(f-n)
	float projB = ProjMatrix[3][2]; // -2fn/(f-n)
	float cameraNear = abs(projB / (projA - 1.0f));
	float cameraFar  = abs(projB / (projA + 1.0f));
	vec3  cameraPos   = vec3(InverseViewMatrix[3]);
	float cameraDist  = length(fragPosWS - cameraPos);
	// Conservative depth-buffer world-space error (2x safety margin)
	float depthError = 2.0f * cameraDist * cameraDist * abs(cameraFar - cameraNear)
	                 / max(cameraNear * cameraFar * 16777216.0f, 1e-10f);
	float depthRel = depthError / max(lightDist, 0.001f);

	float r16fRel  = 1.0f / 512.0f;

	// Take the largest of the three contributors
	float relThreshold = max(texelRel, max(depthRel, r16fRel));

	// Pre-multiply threshold into lightDist for a single comparison per sample
	float biasedLightDist = lightDist * (1.0f - relThreshold);

	float sampleRadius = GetShadowCubeSampleRadius(ShadowMap, ShadowFilterRadius);
	float blockerSearchRadius = GetShadowCubeSampleRadius(ShadowMap, ShadowBlockerSearchRadius);
	float minPenumbra = GetShadowCubeSampleRadius(ShadowMap, ShadowMinPenumbra);
	float maxPenumbra = GetShadowCubeSampleRadius(ShadowMap, ShadowMaxPenumbra);
	float contactBias = lightDist * relThreshold;
	float viewDepth = length(fragPosWS - vec3(InverseViewMatrix[3]));
	float contact = EnableContactShadows
		? SampleDeferredContactShadow(fragPosWS, N, normalize(LightData.Position - fragPosWS), normalOffset, contactBias, viewDepth)
		: 1.0f;

	float lit = SampleShadowCubeFilteredLocal(
		ShadowMap,
		fragToLight,
		biasedLightDist,
		farPlaneDist,
		sampleRadius,
		blockerSearchRadius,
		ShadowBlockerSamples,
		ShadowFilterSamples,
		SoftShadowMode,
		LightSourceRadius,
		minPenumbra,
		maxPenumbra,
		ShadowVogelTapCount) * contact;

	// Write debug state for visualisation (single center sample)
	if (ShadowDebugMode != 0)
	{
		float centerDepth = texture(ShadowMap, normalize(fragToLight)).r * farPlaneDist;
		_dbgShadowMargin = (centerDepth - biasedLightDist) / max(farPlaneDist, 0.001f);
	}
	_dbgShadowLit = lit;

	return lit;
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

	vec3 kS = F;
	vec3 kD = 1.0f - kS;
	kD *= 1.0f - metallic;

	vec3 radiance = lightAttenuation * LightData.Color * LightData.DiffuseIntensity;
	return (kD * albedo / PI + spec) * radiance * NoL;
}
vec3 CalcPointLight(
in vec3 N,
in vec3 V,
in vec3 fragPosWS,
in vec3 albedo,
in vec3 rms,
in vec3 F0)
{
	vec3 L = LightData.Position - fragPosWS;
	float lightDist = length(L);
	float attn = XRENGINE_Attenuate(lightDist, LightData.Radius) * LightData.Brightness;
	L = normalize(L);
	vec3 H = normalize(V + L);

	float NoL = max(dot(N, L), 0.0f);
	if (NoL <= 0.0f) return vec3(0.0f);
	float NoH = max(dot(N, H), 0.0f);
	float NoV = max(dot(N, V), 0.0f);
	float HoV = max(dot(H, V), 0.0f);

	vec3 color = CalcColor(
		NoL, NoH, NoV, HoV,
		attn, albedo, rms, F0);

	float lit = ReadPointShadowMap(LightData.Radius, fragPosWS, N, NoL);

	return color * lit;
}
vec3 CalcTotalLight(
in vec3 fragPosWS,
in vec3 normal,
in vec3 albedo,
in vec3 rms)
{
	normal = normalize(normal);
	float metallic = rms.y;
	vec3 CameraPosition = vec3(InverseViewMatrix[3]);
	vec3 V = normalize(CameraPosition - fragPosWS);
	vec3 F0 = mix(vec3(0.04f), albedo, metallic);
	return CalcPointLight(normal, V, fragPosWS, albedo, rms, F0);
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

	// InverseProjMatrix already encodes the active depth convention. Use raw
	// depth so reversed-Z does not get flipped a second time.
	vec3 fragPosWS = XRENGINE_WorldPosFromDepthRaw(depth, uv, InverseProjMatrix, InverseViewMatrix);

	OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms);

	// Debug visualisation (additive blending still applies; works best with a single point light)
	if (ShadowDebugMode == 1)
	{
		// Shadow-only: white = fully lit, black = fully shadowed
		OutColor = vec3(_dbgShadowLit);
	}
	else if (ShadowDebugMode == 2)
	{
		// Margin heatmap: hue follows the filtered shadow result, intensity follows margin magnitude.
		float intensity = min(abs(_dbgShadowMargin) * 20.0f, 1.0f);
		float clampedLit = clamp(_dbgShadowLit, 0.0f, 1.0f);
		OutColor = vec3((1.0f - clampedLit) * intensity, clampedLit * intensity, 0.0f);
	}
}
