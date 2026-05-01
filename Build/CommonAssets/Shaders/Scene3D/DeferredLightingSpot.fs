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
uniform sampler2D ShadowMap; //Spot Shadow Map
uniform sampler2D SpotShadowAtlas;

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 ViewProjectionMatrix;
uniform float ShadowBase = 0.2f;
uniform float ShadowMult = 1.0f;
uniform float ShadowBiasMin = 0.0001f;
uniform float ShadowBiasMax = 0.07f;
uniform vec4 ShadowBiasParams = vec4(1.0f, 2.0f, 1.0f, 0.0f); // depth texels, slope texels, normal texels, reserved
uniform bool LightHasShadowMap = true; // Added
uniform bool SpotShadowAtlasEnabled = false;
uniform int SpotShadowAtlasRecordIndex = -1;
uniform int SpotShadowAtlasFallbackMode = 1;
uniform vec4 SpotShadowAtlasUvScaleBias = vec4(1.0f, 1.0f, 0.0f, 0.0f);
uniform vec4 SpotShadowAtlasDepthParams = vec4(0.1f, 1.0f, 0.0f, 1.0f); // near, far, local texel size, fallback
uniform int ShadowSamples = 8;
uniform int ShadowBlockerSamples = 8;
uniform int ShadowFilterSamples = 8;
uniform int ShadowVogelTapCount = 5;
uniform float ShadowFilterRadius = 0.0012f;
uniform float ShadowBlockerSearchRadius = 0.1f;
uniform float ShadowMinPenumbra = 0.0002f;
uniform float ShadowMaxPenumbra = 0.05f;
uniform int SoftShadowMode = 2;
uniform float LightSourceRadius = 0.1f;
uniform bool EnableContactShadows = true;
uniform float ContactShadowDistance = 0.1f;
uniform int ContactShadowSamples = 16;
uniform float ContactShadowThickness = 1.0f;
uniform float ContactShadowFadeStart = 10.0f;
uniform float ContactShadowFadeEnd = 40.0f;
uniform float ContactShadowNormalOffset = 0.036f;
uniform float ContactShadowJitterStrength = 1.0f;
uniform vec4 ShadowMomentParams0 = vec4(0.00002f, 0.2f, 5.0f, 5.0f); // min variance, light bleed reduction, positive exponent, negative exponent
uniform vec4 ShadowMomentDepthParams = vec4(0.1f, 1.0f, 0.0f, 0.0f); // near, far, mip bias, use mipmaps
// Debug: 0=normal, 1=shadow-only (white=lit), 2=margin heatmap (green=lit, red=shadow)
uniform int ShadowDebugMode = 0;

struct SpotLight
{
    vec3 Color;
    float DiffuseIntensity;
    mat4 WorldToLightInvViewMatrix;
	mat4 WorldToLightProjMatrix;
	mat4 WorldToLightSpaceMatrix;  // Pre-computed View * Proj for shadow mapping

    vec3 Position;
    vec3 Direction;
    float Radius;
    float Brightness;
    float Exponent;

    float InnerCutoff;
    float OuterCutoff;
};
uniform SpotLight LightData;

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

float GetShadowBias(in float NoL)
{
    float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}

float LinearizeSpotShadowDepth01(float depth, float nearZ, float farZ)
{
	float n = max(nearZ, 0.0001f);
	float f = max(farZ, n + 0.0001f);
	float z = depth * 2.0f - 1.0f;
	float linearZ = (2.0f * n * f) / (f + n - z * (f - n));
	return clamp((linearZ - n) / (f - n), 0.0f, 1.0f);
}

float GetSpotDistanceAlongAxis(in vec3 fragPosWS)
{
	return max(dot(fragPosWS - LightData.Position, normalize(LightData.Direction)), 0.0001f);
}

float GetSpotShadowWorldTexelSize(in vec3 fragPosWS, in float texelUvSize)
{
	float cosOuter = clamp(LightData.OuterCutoff, 0.001f, 0.999999f);
	float tanOuter = sqrt(max(1.0f - cosOuter * cosOuter, 0.0f)) / cosOuter;
	return 2.0f * GetSpotDistanceAlongAxis(fragPosWS) * tanOuter * max(texelUvSize, 1e-7f);
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

// Global debug state written by ReadShadowMap2D for visualization
float _dbgShadowLit = 1.0f;
float _dbgShadowMargin = 1.0f;

// returns1 lit,0 shadow
float ReadShadowMap2D(in vec3 fragPosWS, in vec3 N, in float NoL, in mat4 lightMatrix)
{
  if (NoL <= 0.0f) return 0.0f;
	float nearZ = SpotShadowAtlasEnabled ? SpotShadowAtlasDepthParams.x : ShadowMomentDepthParams.x;
	float farZ = SpotShadowAtlasEnabled ? SpotShadowAtlasDepthParams.y : ShadowMomentDepthParams.y;
	float localTexelSize = SpotShadowAtlasEnabled && SpotShadowAtlasDepthParams.z > 0.0f
		? SpotShadowAtlasDepthParams.z
		: 1.0f / max(float(textureSize(ShadowMap, 0).x), 1.0f);
	float worldTexelSize = GetSpotShadowWorldTexelSize(fragPosWS, localTexelSize);
	float receiverOffset = worldTexelSize * max(ShadowBiasParams.z, 0.0f);
	float constantBias = XRENGINE_PerspectiveDepthBiasForWorldOffset(
		worldTexelSize * max(ShadowBiasParams.x, 0.0f),
		GetSpotDistanceAlongAxis(fragPosWS),
		nearZ,
		farZ);
	float viewDepth = abs((ViewMatrix * vec4(fragPosWS, 1.0f)).z);

	if (!LightHasShadowMap)
	{
		float fallbackContact = EnableContactShadows
			? SampleDeferredContactShadow(fragPosWS, N, normalize(LightData.Position - fragPosWS), receiverOffset, constantBias, viewDepth)
			: 1.0f;
		float fallbackLit = SpotShadowAtlasFallbackMode == 2 ? fallbackContact : 1.0f;
		_dbgShadowLit = fallbackLit;
		_dbgShadowMargin = 1.0f;
		return fallbackLit;
	}

	vec3 offsetPosWS = fragPosWS + N * receiverOffset;
	vec4 fragPosLightSpace = lightMatrix * vec4(offsetPosWS, 1.0f);
	vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
	fragCoord = fragCoord * 0.5f + 0.5f;
	if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
		fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
		fragCoord.z < 0.0f || fragCoord.z > 1.0f)
 return 1.0f;

	float bias = XRENGINE_ComputeShadowDepthBias(
		fragCoord,
		vec2(localTexelSize),
		ShadowFilterRadius,
		constantBias,
		max(ShadowBiasParams.y, 0.0f));
	float contact = EnableContactShadows
		? SampleDeferredContactShadow(fragPosWS, N, normalize(LightData.Position - fragPosWS), receiverOffset, bias, viewDepth)
		: 1.0f;

	float lit = 1.0f;
	if (SpotShadowAtlasEnabled)
	{
		vec2 atlasUv = fragCoord.xy * SpotShadowAtlasUvScaleBias.xy + SpotShadowAtlasUvScaleBias.zw;
		float atlasRadiusScale = max(SpotShadowAtlasUvScaleBias.x, SpotShadowAtlasUvScaleBias.y);
		float atlasNearZ = SpotShadowAtlasDepthParams.x;
		float atlasFarZ = SpotShadowAtlasDepthParams.y;
		float atlasDepth = LinearizeSpotShadowDepth01(
			fragCoord.z,
			atlasNearZ,
			atlasFarZ);
		float atlasFilterRadius = ShadowFilterRadius * atlasRadiusScale;
		float atlasBlockerRadius = ShadowBlockerSearchRadius * atlasRadiusScale;
		vec2 atlasTexelSize = max(vec2(SpotShadowAtlasDepthParams.z) * SpotShadowAtlasUvScaleBias.xy, vec2(1e-7f));
		float atlasBias = XRENGINE_ComputeShadowDepthBias(
			vec3(atlasUv, fragCoord.z),
			atlasTexelSize,
			atlasFilterRadius,
			constantBias,
			max(ShadowBiasParams.y, 0.0f));
		lit = XRENGINE_SampleLinearDepthShadowMapFilteredAsPerspective(
			SpotShadowAtlas,
			vec3(atlasUv, atlasDepth),
			fragCoord.z,
			atlasBias,
			ShadowBlockerSamples,
			ShadowFilterSamples,
			atlasFilterRadius,
			atlasBlockerRadius,
			SoftShadowMode,
			LightSourceRadius * atlasRadiusScale,
			ShadowMinPenumbra * atlasRadiusScale,
			ShadowMaxPenumbra * atlasRadiusScale,
			ShadowVogelTapCount,
			atlasNearZ,
			atlasFarZ) * contact;

		if (ShadowDebugMode != 0)
		{
			float centerDepth = XRENGINE_LinearDepth01ToPerspectiveDepth(texture(SpotShadowAtlas, atlasUv).r, atlasNearZ, atlasFarZ);
			_dbgShadowMargin = centerDepth - (fragCoord.z - atlasBias);
		}
	}
	else
	{
		if (ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH)
		{
			float receiverDepth = XRENGINE_LinearizeShadowDepth01(
				fragCoord.z,
				ShadowMomentDepthParams.x,
				ShadowMomentDepthParams.y);
			float momentReceiverDepth = clamp(receiverDepth - min(bias, 0.01f), 0.0f, 1.0f);
			lit = XRENGINE_SampleShadowMoment2D(
				ShadowMap,
				fragCoord.xy,
				momentReceiverDepth,
				ShadowMapEncoding,
				ShadowMomentParams0.x,
				ShadowMomentParams0.y,
				ShadowMomentParams0.z,
				ShadowMomentParams0.w,
				ShadowMomentDepthParams.z) * contact;

			if (ShadowDebugMode != 0)
			{
				_dbgShadowMargin = XRENGINE_EstimateShadowMomentMargin(
					ShadowMap,
					fragCoord.xy,
					momentReceiverDepth,
					ShadowMapEncoding,
					ShadowMomentParams0.z,
					ShadowMomentParams0.w);
			}
		}
		else
		{
			lit = SampleShadowMapFilteredLocal(
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

			if (ShadowDebugMode != 0)
			{
				float centerDepth = texture(ShadowMap, fragCoord.xy).r;
				_dbgShadowMargin = (centerDepth - (fragCoord.z - bias)) / max(1.0f, 0.001f);
			}
		}
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
	vec3 kD = (1.0f - kS) * (1.0f - metallic);
	vec3 radiance = lightAttenuation * LightData.Color * LightData.DiffuseIntensity;
	return (kD * albedo / PI + spec) * radiance * NoL;
}
vec3 CalcLight(
in vec3 N,
in vec3 V,
in vec3 fragPosWS,
in vec3 albedo,
in vec3 rms,
in vec3 F0)
{
	vec3 L = LightData.Position - fragPosWS;
	float lightDist = length(L);
	L = normalize(L);

	//OuterCutoff is the smaller value, despite being a larger angle
	//cos(90) == 0
	//cos(0) == 1

	float cosine = dot(L, -normalize(LightData.Direction));

	if (cosine <= LightData.OuterCutoff)
		return vec3(0.0f);

	float clampedCosine = clamp(cosine, LightData.OuterCutoff, LightData.InnerCutoff);

	//Subtract smaller value and divide by range to normalize value
	float time = (clampedCosine - LightData.OuterCutoff) / (LightData.InnerCutoff - LightData.OuterCutoff);

	//Make transition smooth rather than linear
	float spotAmt = smoothstep(0.0f, 1.0f, time);
	float distAttn = XRENGINE_Attenuate(lightDist, LightData.Radius) * LightData.Brightness;
	float attn = spotAmt * distAttn * pow(clampedCosine, LightData.Exponent);

	vec3 H = normalize(V + L);
	float NoL = max(dot(N, L), 0.0f);
	if (NoL <= 0.0f) return vec3(0.0f); 
	float NoH = max(dot(N, H), 0.0f);
	float NoV = max(dot(N, V), 0.0f);
	float HoV = max(dot(H, V), 0.0f);

	vec3 color = CalcColor(
		NoL, NoH, NoV, HoV,
		attn, albedo, rms, F0);

	float lit = ReadShadowMap2D(
		fragPosWS, N, NoL,
		LightData.WorldToLightSpaceMatrix);

	return color * lit;
}
vec3 CalcTotalLight(
in vec3 CameraPosition,
in vec3 fragPosWS,
in vec3 normal,
in vec3 albedo,
in vec3 rms)
{
	float metallic = rms.y;
	vec3 V = normalize(CameraPosition - fragPosWS);
	vec3 F0 = mix(vec3(0.04f), albedo, metallic);
	return CalcLight(normal, V, fragPosWS, albedo, rms, F0);
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

	vec3 CameraPosition = vec3(InverseViewMatrix[3]);
	OutColor = CalcTotalLight(CameraPosition, fragPosWS, normal, albedo, rms);

	// Debug overlays (ShadowDebugMode: 0=off, 1=raw shadow, 2=margin heatmap)
	if (ShadowDebugMode == 1)
	{
		OutColor = vec3(_dbgShadowLit);
	}
	else if (ShadowDebugMode == 2)
	{
		float m = _dbgShadowMargin;
		OutColor = m >= 0.0f
			? vec3(0.0f, min(m * 20.0f, 1.0f), 0.0f)   // green = lit margin
			: vec3(min(-m * 20.0f, 1.0f), 0.0f, 0.0f);  // red   = shadow margin
	}
}
