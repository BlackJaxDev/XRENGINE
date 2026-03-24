#version 450

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

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

uniform float MinFade = 500.0f;
uniform float MaxFade = 10000.0f;
uniform float ShadowBase = 2.0f;
uniform float ShadowMult = 3.0f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;
uniform bool LightHasShadowMap = true; // Added
uniform int ShadowSamples = 16;
uniform float ShadowFilterRadius = 0.001f;
uniform bool EnablePCSS = true;

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

float GetShadowBias(in float NoL)
{
    float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}
// returns1 lit,0 shadow
float ReadShadowMap2D(in vec3 fragPosWS, in vec3 N, in float NoL, in mat4 lightMatrix)
{
  if (!LightHasShadowMap) return 1.0f;
  if (NoL <= 0.0f) return 0.0f;
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

	if (EnablePCSS)
	{
		int sampleCount = ShadowSamples > 1 ? ShadowSamples : 16;
		return SampleShadowMapSoftLocal(ShadowMap, fragCoord, bias, sampleCount, ShadowFilterRadius);
	}

	return SampleShadowMapPCFLocal(ShadowMap, fragCoord, bias, 3);
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
	float distAttn = XRENGINE_Attenuate(lightDist / LightData.Brightness, LightData.Radius / LightData.Brightness);
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
	vec3 fragPosWS = XRENGINE_WorldPosFromDepthRaw(depth, uv, inverse(ProjMatrix), InverseViewMatrix);

  	float fadeRange = MaxFade - MinFade;
	vec3 CameraPosition = vec3(InverseViewMatrix[3]);
  	float dist = length(CameraPosition - fragPosWS);
  	float strength = smoothstep(1.0f, 0.0f, clamp((dist - MinFade) / fadeRange, 0.0f, 1.0f));
  	OutColor = strength * CalcTotalLight(CameraPosition, fragPosWS, normal, albedo, rms);
}
