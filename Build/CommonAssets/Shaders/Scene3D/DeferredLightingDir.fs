#version 450

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
uniform bool UseCascadedDirectionalShadows = false;
uniform bool LightHasShadowMap = true;
uniform bool EnableCascadedShadows = true;
uniform bool DebugCascadeColors = false;

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
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 ViewProjectionMatrix;
uniform float ShadowBase = 0.035f;
uniform float ShadowMult = 1.221f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;
uniform int ShadowSamples = 4;
uniform float ShadowFilterRadius = 0.0012f;
uniform int SoftShadowMode = 1;
uniform float LightSourceRadius = 0.01f;
uniform bool EnableContactShadows = true;
uniform float ContactShadowDistance = 0.1f;
uniform int ContactShadowSamples = 4;

struct DirLight
{
	vec3 Color;
	float DiffuseIntensity;
	mat4 WorldToLightInvViewMatrix;
	mat4 WorldToLightProjMatrix;
	mat4 WorldToLightSpaceMatrix;  // Pre-computed View * Proj for shadow mapping
	vec3 Direction;
	float CascadeSplits[MAX_CASCADES];
	mat4 CascadeMatrices[MAX_CASCADES];
	int CascadeCount;
};
uniform DirLight LightData;

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

float SampleShadowMapFilteredLocal(in sampler2D shadowMap, in vec3 shadowCoord, in float bias, in int requestedSamples, in float filterRadius, in int softMode, in float lightSourceRadius)
{
	int sampleCount = clamp(requestedSamples, 1, 16);
	if (sampleCount <= 1)
		return SampleShadowMapSimpleLocal(shadowMap, shadowCoord, bias);
	if (softMode == 2)
		return SampleShadowMapCHSSLocal(shadowMap, shadowCoord, bias, sampleCount, filterRadius, lightSourceRadius);
	if (softMode == 1)
		return SampleShadowMapSoftLocal(shadowMap, shadowCoord, bias, sampleCount, filterRadius);
	if (sampleCount <= 4)
		return SampleShadowMapTent4Local(shadowMap, shadowCoord, bias, filterRadius);
	return SampleShadowMapPCFLocal(shadowMap, shadowCoord, bias, 3);
}

float SampleShadowMapArrayFilteredLocal(in sampler2DArray shadowMap, in vec3 shadowCoord, in float layer, in float bias, in int requestedSamples, in float filterRadius, in int softMode, in float lightSourceRadius)
{
	int sampleCount = clamp(requestedSamples, 1, 16);
	if (sampleCount <= 1)
		return SampleShadowMapArraySimpleLocal(shadowMap, shadowCoord, layer, bias);
	if (softMode == 2)
		return SampleShadowMapArrayCHSSLocal(shadowMap, shadowCoord, layer, bias, sampleCount, filterRadius, lightSourceRadius);
	if (softMode == 1)
		return SampleShadowMapArraySoftLocal(shadowMap, shadowCoord, layer, bias, sampleCount, filterRadius);
	if (sampleCount <= 4)
		return SampleShadowMapArrayTent4Local(shadowMap, shadowCoord, layer, bias, filterRadius);
	return SampleShadowMapArrayPCFLocal(shadowMap, shadowCoord, layer, bias, 3);
}

float GetShadowBias(in float NoL)
{
	float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
	return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}

// Screen-space contact shadows: marches along the light direction in screen space
// and tests against the depth buffer. Inherently camera-stable because it operates
// on the current frame's depth buffer rather than shadow map texels.
float SampleContactShadowScreenSpaceLocal(vec3 fragPosWS, vec3 lightDirWS, float maxDistance, int numSteps)
{
	if (maxDistance <= 0.0f || numSteps <= 0) return 1.0f;
	int steps = clamp(numSteps, 1, 32);

	vec3 fragPosVS = (ViewMatrix * vec4(fragPosWS, 1.0f)).xyz;
	vec3 lightDirVS = normalize(mat3(ViewMatrix) * lightDirWS);
	vec3 rayEndVS = fragPosVS + lightDirVS * maxDistance;
	vec3 rayEndWS = fragPosWS + lightDirWS * maxDistance;

	vec4 startClip = ViewProjectionMatrix * vec4(fragPosWS, 1.0f);
	vec4 endClip = ViewProjectionMatrix * vec4(rayEndWS, 1.0f);
	vec3 startSS = startClip.xyz / startClip.w * 0.5f + 0.5f;
	vec3 endSS = endClip.xyz / endClip.w * 0.5f + 0.5f;
	vec3 rayDelta = endSS - startSS;

	if (length(rayDelta.xy) < 1e-6f) return 1.0f;

	// Interleaved gradient noise (Jimenez 2014)
	float noise = fract(52.9829189f * fract(dot(gl_FragCoord.xy, vec2(0.06711056f, 0.00583715f))));

	float stepT = 1.0f / float(steps);
	float occlusion = 0.0f;

	for (int i = 0; i < 32; ++i)
	{
		if (i >= steps) break;

		float t = (float(i) + 0.5f + (noise - 0.5f)) * stepT;
		vec3 sampleSS = startSS + rayDelta * t;

		if (sampleSS.x < 0.0f || sampleSS.x > 1.0f ||
			sampleSS.y < 0.0f || sampleSS.y > 1.0f)
			continue;

#ifdef XRENGINE_MSAA_DEFERRED
		float sceneDepth = texelFetch(DepthView, ivec2(sampleSS.xy * vec2(ScreenWidth, ScreenHeight)), 0).r;
#else
		float sceneDepth = texture(DepthView, sampleSS.xy).r;
#endif

		if (sceneDepth >= 1.0f) continue;

		float rayDepth = sampleSS.z;
		float depthDiff = rayDepth - sceneDepth;

		float stepDepthSpan = abs(rayDelta.z) * stepT;
		float maxThickness = max(stepDepthSpan * 5.0f, 0.0005f);

		if (depthDiff > 0.0f && depthDiff < maxThickness)
		{
			float hitWeight = 1.0f - smoothstep(0.0f, maxThickness, depthDiff);
			float distFade = 1.0f - t;
			occlusion = max(occlusion, hitWeight * distFade);
		}
	}

	return 1.0f - occlusion;
}

int ResolveContactShadowSampleCountLocal(int requestedSamples, float viewDepth, float contactDistance)
{
	if (requestedSamples <= 0 || contactDistance <= 0.0f) return 0;
	int clampedSamples = clamp(requestedSamples, 1, 32);
	float normalizedDepth = max(viewDepth, contactDistance);
	float depthScale = clamp((contactDistance * 24.0f) / normalizedDepth, 0.35f, 1.0f);
	return max(1, int(ceil(float(clampedSamples) * depthScale)));
}

float ViewDepthFromWorldPos(in vec3 fragPosWS)
{
	vec4 viewPos = ViewMatrix * vec4(fragPosWS, 1.0f);
	return abs(viewPos.z);
}

int GetCascadeIndex(in vec3 fragPosWS)
{
	if (!UseCascadedDirectionalShadows || !EnableCascadedShadows || LightData.CascadeCount <= 0)
		return -1;

	float viewDepth = ViewDepthFromWorldPos(fragPosWS);
	int cascadeCount = min(LightData.CascadeCount, MAX_CASCADES);
	for (int i = 0; i < cascadeCount; ++i)
	{
		if (viewDepth <= LightData.CascadeSplits[i])
			return i;
	}

	return cascadeCount - 1;
}

//0 is fully in shadow, 1 is fully lit
float ReadShadowMap2D(in vec3 fragPosWS, in vec3 N, in float NoL, in mat4 lightMatrix)
{
		if (!LightHasShadowMap)
			return 1.0f;

		vec3 offsetPosWS = fragPosWS + N * ShadowBiasMax;
		vec4 fragPosLightSpace = lightMatrix * vec4(offsetPosWS, 1.0f);
		vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
		fragCoord = fragCoord * 0.5f + 0.5f;

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
			fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
			fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return 1.0f;

		//Create bias depending on angle of normal to the light
		float bias = GetShadowBias(NoL);
		int contactSampleCount = ResolveContactShadowSampleCountLocal(
			ContactShadowSamples,
			ViewDepthFromWorldPos(fragPosWS),
			ContactShadowDistance);
		float contact = EnableContactShadows
			? SampleContactShadowScreenSpaceLocal(
				fragPosWS,
				normalize(-LightData.Direction),
				ContactShadowDistance,
				contactSampleCount)
			: 1.0f;

		return SampleShadowMapFilteredLocal(
			ShadowMap,
			fragCoord,
			bias,
			ShadowSamples,
			ShadowFilterRadius,
			SoftShadowMode,
			LightSourceRadius) * contact;
}

float ReadCascadeShadowMap(in vec3 fragPosWS, in vec3 N, in float NoL, in int cascadeIndex)
{
		if (!LightHasShadowMap)
			return 1.0f;

		mat4 lightMatrix = LightData.CascadeMatrices[cascadeIndex];
		vec3 offsetPosWS = fragPosWS + N * ShadowBiasMax;
		vec4 fragPosLightSpace = lightMatrix * vec4(offsetPosWS, 1.0f);
		vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
		fragCoord = fragCoord * 0.5f + 0.5f;

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f ||
			fragCoord.y < 0.0f || fragCoord.y > 1.0f ||
			fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return ReadShadowMap2D(fragPosWS, N, NoL, LightData.WorldToLightSpaceMatrix);

		float bias = GetShadowBias(NoL);
		int contactSampleCount = ResolveContactShadowSampleCountLocal(
			ContactShadowSamples,
			ViewDepthFromWorldPos(fragPosWS),
			ContactShadowDistance);
		float contact = EnableContactShadows
			? SampleContactShadowScreenSpaceLocal(
				fragPosWS,
				normalize(-LightData.Direction),
				ContactShadowDistance,
				contactSampleCount)
			: 1.0f;
		float filterRadius = ShadowFilterRadius * (1.0f + float(cascadeIndex) * 0.35f);
		return SampleShadowMapArrayFilteredLocal(
			ShadowMapArray,
			fragCoord,
			float(cascadeIndex),
			bias,
			ShadowSamples,
			filterRadius,
			SoftShadowMode,
			LightSourceRadius) * contact;
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
in vec3 F0)
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

		int cascadeIndex = GetCascadeIndex(fragPosWS);
		float shadow = cascadeIndex >= 0
			? ReadCascadeShadowMap(fragPosWS, N, NoL, cascadeIndex)
			: ReadShadowMap2D(fragPosWS, N, NoL, LightData.WorldToLightSpaceMatrix);

		vec3 result = color * shadow;

		// Debug overlay: tint output with cascade color when enabled
		if (DebugCascadeColors && cascadeIndex >= 0)
		{
			vec3 debugColor = CascadeDebugColorTable[cascadeIndex % MAX_CASCADES];
			result = mix(result, debugColor * max(shadow, 0.15), 0.5);
		}

		return result;
}
vec3 CalcTotalLight(
in vec3 fragPosWS,
in vec3 normal,
in vec3 albedo,
in vec3 rms,
in vec3 cameraPosition)
{
		float metallic = rms.y;
		vec3 V = normalize(cameraPosition - fragPosWS);
		vec3 F0 = mix(vec3(0.04f), albedo, metallic);
		return CalcLight(normal, V, fragPosWS, albedo, rms, F0);
}
void main()
{
		vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
#ifdef XRENGINE_MSAA_DEFERRED
		ivec2 coord = ivec2(gl_FragCoord.xy);
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
		if (depth >= 1.0f)
		{
			OutColor = vec3(0.0f);
			return;
		}

		//Resolve world fragment position using depth and screen UV
		vec3 fragPosWS = XRENGINE_WorldPosFromDepthRaw(depth, uv, InverseProjMatrix, InverseViewMatrix);
		vec3 cameraPosition = InverseViewMatrix[3].xyz;

		OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms, cameraPosition);
}
