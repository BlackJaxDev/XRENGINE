#version 450

#pragma snippet "NormalEncoding"

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;
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
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

uniform float MinFade = 500.0f;
uniform float MaxFade = 10000.0f;
uniform float ShadowBase = 1.0f;
uniform float ShadowMult = 1.0f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;

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

float GetShadowBias(in float NoL)
{
	float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
	return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}
float Attenuate(in float dist, in float radius)
{
	return pow(clamp(1.0f - pow(dist / radius, 4.0f), 0.0f, 1.0f), 2.0f) / (dist * dist + 1.0f);
}

float ViewDepthFromWorldPos(in vec3 fragPosWS)
{
	mat4 viewMatrix = inverse(InverseViewMatrix);
	vec4 viewPos = viewMatrix * vec4(fragPosWS, 1.0f);
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
		//Move the fragment position into light space
		vec4 fragPosLightSpace = lightMatrix * vec4(fragPosWS, 1.0f);
		vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
		fragCoord = fragCoord * 0.5f + 0.5f;

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f || fragCoord.y < 0.0f || fragCoord.y > 1.0f || fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return 1.0f;

		//Create bias depending on angle of normal to the light
		float bias = GetShadowBias(NoL);

		//Hard shadow
		float depth = texture(ShadowMap, fragCoord.xy).r;
		float shadow1 = (fragCoord.z - bias) > depth ? 0.0f : 1.0f;

		//PCF shadow
		float shadow = 0.0f;
		vec2 texelSize = 1.0f / textureSize(ShadowMap, 0);
		for (int x = -1; x <= 1; ++x)
		{
				for (int y = -1; y <= 1; ++y)
				{
						float pcfDepth = texture(ShadowMap, fragCoord.xy + vec2(x, y) * texelSize).r;
						shadow += (fragCoord.z - bias > pcfDepth) ? 0.0f : 1.0f;
				}
		}
		shadow *= 0.111111111f; //divided by 9

		float dist = fragCoord.z - depth;
		float maxBlurDist = 0.1f;
		float normDist = clamp(dist, 0.0f, maxBlurDist) / maxBlurDist;
		shadow = mix(shadow1, shadow, normDist);

		return shadow;
}

float ReadCascadeShadowMap(in vec3 fragPosWS, in vec3 N, in float NoL, in int cascadeIndex)
{
		mat4 lightMatrix = LightData.CascadeMatrices[cascadeIndex];
		vec4 fragPosLightSpace = lightMatrix * vec4(fragPosWS, 1.0f);
		vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
		fragCoord = fragCoord * 0.5f + 0.5f;

		if (fragCoord.x < 0.0f || fragCoord.x > 1.0f || fragCoord.y < 0.0f || fragCoord.y > 1.0f || fragCoord.z < 0.0f || fragCoord.z > 1.0f)
			return 1.0f;

		float bias = GetShadowBias(NoL);
		float hardDepth = texture(ShadowMapArray, vec3(fragCoord.xy, float(cascadeIndex))).r;
		float hardShadow = (fragCoord.z - bias) > hardDepth ? 0.0f : 1.0f;

		float shadow = 0.0f;
		vec2 texelSize = 1.0f / vec2(textureSize(ShadowMapArray, 0).xy);
		for (int x = -1; x <= 1; ++x)
		{
			for (int y = -1; y <= 1; ++y)
			{
				float pcfDepth = texture(ShadowMapArray, vec3(fragCoord.xy + vec2(x, y) * texelSize, float(cascadeIndex))).r;
				shadow += (fragCoord.z - bias > pcfDepth) ? 0.0f : 1.0f;
			}
		}
		shadow *= 0.111111111f;

		float dist = fragCoord.z - hardDepth;
		float maxBlurDist = 0.1f;
		float normDist = clamp(dist, 0.0f, maxBlurDist) / maxBlurDist;
		return mix(hardShadow, shadow, normDist);
}
//Trowbridge-Reitz GGX
float SpecD_TRGGX(in float NoH2, in float a2)
{
		float num    = a2;
		float denom  = (NoH2 * (a2 - 1.0f) + 1.0f);
		denom        = PI * denom * denom;

		return num / denom;
}
float SpecG_SchlickGGX(in float NoV, in float k)
{
		float num   = NoV;
		float denom = NoV * (1.0f - k) + k;

		return num / denom;
}
float SpecG_Smith(in float NoV, in float NoL, in float k)
{
		float ggx1 = SpecG_SchlickGGX(NoV, k);
		float ggx2 = SpecG_SchlickGGX(NoL, k);
		return ggx1 * ggx2;
}
vec3 SpecF_Schlick(in float VoH, in vec3 F0)
{
		float pow = pow(1.0f - VoH, 5.0f);
		return F0 + (1.0f - F0) * pow;
}
vec3 SpecF_SchlickApprox(in float VoH, in vec3 F0)
{
		//Spherical Gaussian Approximation
		float pow = exp2((-5.55473f * VoH - 6.98316f) * VoH);
		return F0 + (1.0f - F0) * pow;
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

		float a = roughness * roughness;
		float k = roughness + 1.0f;
		k = k * k * 0.125f; //divide by 8

		float D = SpecD_TRGGX(NoH * NoH, a * a);
		float G = SpecG_Smith(NoV, NoL, k);
		vec3  F = SpecF_SchlickApprox(HoV, F0);

		//Cook-Torrance Specular
		float denom = 4.0f * NoV * NoL + 0.0001f;
		vec3 spec =  specular * D * G * F / denom;

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
vec3 WorldPosFromDepth(in float depth, in vec2 uv)
{
		vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
		vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
		viewSpacePosition /= viewSpacePosition.w;
		return (InverseViewMatrix * viewSpacePosition).xyz;
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
		vec3 fragPosWS = WorldPosFromDepth(depth, uv);
		vec3 cameraPosition = InverseViewMatrix[3].xyz;
		float dist = length(cameraPosition - fragPosWS);
		float fadeStrength = 1.0f;
		if (MaxFade > MinFade)
		{
			float fadeRange = MaxFade - MinFade;
			fadeStrength = smoothstep(1.0f, 0.0f, clamp((dist - MinFade) / fadeRange, 0.0f, 1.0f));
		}

		OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms, cameraPosition) * fadeStrength;
}
