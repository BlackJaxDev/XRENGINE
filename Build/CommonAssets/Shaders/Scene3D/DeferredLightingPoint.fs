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
uniform samplerCube ShadowMap; //Point Shadow Map

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;

uniform float MinFade = 500.0f;
uniform float MaxFade = 10000.0f;
uniform float ShadowBase = 1.0f;
uniform float ShadowMult = 2.5f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;
uniform bool LightHasShadowMap = true; // Added
uniform int ShadowSamples = 4;
uniform float ShadowFilterRadius = 0.001f;
uniform bool EnablePCSS = true;

struct PointLight
{
    vec3 Color;
    float DiffuseIntensity;
    vec3 Position;
    float Radius;
    float Brightness;
};
uniform PointLight LightData;

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

float SampleShadowCubePCFLocal(in samplerCube shadowMap, in vec3 shadowDir, in float compareDepth, in float bias, in float farPlaneDist, in float sampleRadius)
{
	float lit = 0.0f;

	for (int i = 0; i < 20; ++i)
	{
		vec3 sampleDir = normalize(shadowDir + LocalShadowCubeKernel[i] * sampleRadius);
		float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
		lit += (compareDepth - bias) <= sampleDepth ? 1.0f : 0.0f;
	}

	return lit / 20.0f;
}

float GetShadowBias(in float NoL)
{
    float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}

float SampleShadowCubeFilteredLocal(in samplerCube shadowMap, in vec3 shadowDir, in float compareDepth, in float bias, in float farPlaneDist, in float sampleRadius, in int requestedSamples, in bool enableSoft)
{
	int sampleCount = clamp(requestedSamples, 1, 20);
	if (sampleCount <= 1)
	{
		float sampleDepth = texture(shadowMap, normalize(shadowDir)).r * farPlaneDist;
		return (compareDepth - bias) <= sampleDepth ? 1.0f : 0.0f;
	}

	if (enableSoft || sampleCount <= 4)
	{
		float lit = 0.0f;
		for (int i = 0; i < 20; ++i)
		{
			if (i >= sampleCount)
				break;

			vec3 sampleDir = normalize(shadowDir + LocalShadowCubeKernel[i] * sampleRadius);
			float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
			lit += (compareDepth - bias) <= sampleDepth ? 1.0f : 0.0f;
		}

		return lit / float(sampleCount);
	}

	return SampleShadowCubePCFLocal(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius);
}

// returns1 lit,0 shadow
float ReadPointShadowMap(in float farPlaneDist, in vec3 fragToLightWS, in float lightDist, in float NoL)
{
    if (!LightHasShadowMap) return 1.0f;
    float bias = GetShadowBias(NoL);
	float sampleRadius = max(0.035f, ShadowFilterRadius * 24.0f);
	return SampleShadowCubeFilteredLocal(
		ShadowMap,
		fragToLightWS,
		lightDist,
		bias,
		farPlaneDist,
		sampleRadius,
		ShadowSamples,
		EnablePCSS);
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

	float lit = ReadPointShadowMap(LightData.Radius, -L, lightDist, NoL);

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

	if (XRENGINE_ResolveDepth(depth) >= 1.0f)
	{
		OutColor = vec3(0.0f);
		return;
	}

	//Resolve world fragment position using depth and screen UV
	vec3 fragPosWS = XRENGINE_WorldPosFromDepth(depth, uv, InverseProjMatrix, InverseViewMatrix);

	//float fadeRange = MaxFade - MinFade;
	//float dist = length(CameraPosition - fragPosWS);
	//float strength = smoothstep(1.0f, 0.0f, clamp((dist - MinFade) / fadeRange, 0.0f, 1.0f));
	OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms);
}
