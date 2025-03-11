#version 450

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;

layout(location = 0) out vec3 OutColor; //Diffuse lighting output
layout(location = 0) in vec3 FragPos;

uniform sampler2D Texture0; //AlbedoOpacity
uniform sampler2D Texture1; //Normal
uniform sampler2D Texture2; //PBR: Roughness, Metallic, Specular, Index of refraction
uniform sampler2D Texture3; //Depth
uniform samplerCube ShadowMap; //Point Shadow Map

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

uniform float MinFade = 500.0f;
uniform float MaxFade = 1000.0f;
uniform float ShadowBase = 1.0f;
uniform float ShadowMult = 2.5f;
uniform float ShadowBiasMin = 0.05f;
uniform float ShadowBiasMax = 10.0f;

struct PointLight
{
    vec3 Color;
    float DiffuseIntensity;
    vec3 Position;
    float Radius;
    float Brightness;
};
uniform PointLight LightData;

float GetShadowBias(in float NoL)
{
    float mapped = pow(ShadowBase * (1.0f - NoL), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}
//0 is fully in shadow, 1 is fully lit
float ReadPointShadowMap(in float farPlaneDist, in vec3 fragToLightWS, in float lightDist, in float NoL)
{
    float bias = GetShadowBias(NoL);

    //Hard shadow
    float closestDepth = texture(ShadowMap, fragToLightWS).r * farPlaneDist;
    float shadow = lightDist - bias > closestDepth ? 0.0f : 1.0f;

    //PCF shadow
    //float shadow = 0.0;
    //vec2 texelSize = 1.0f / textureSize(map, 0);
    //for (int x = -1; x <= 1; ++x)
    //{
    //    for (int y = -1; y <= 1; ++y)
    //    {
    //           for (int z = -1; z <= 1; ++z)
    //        {
    //            float pcfDepth = texture(map, fragToLightWS + vec3(x, y, z) * texelSize).r * farPlaneDist;
    //            shadow += fragCoord.z - bias > pcfDepth ? 0.0f : 1.0f;
    //        }
    //    }
    //}
    //shadow *= 0.111111111f; //divided by 9

    return shadow;
}
float Attenuate(in float dist, in float radius)
{
    return pow(clamp(1.0f - pow(dist / radius, 4.0f), 0.0f, 1.0f), 2.0f) / (dist * dist + 1.0f);
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
//vec3 SpecF_SchlickRoughness(in float VoH, in vec3 F0, in float roughness)
//{
//	float pow = pow(1.0f - VoH, 5.0f);
//	return F0 + (max(vec3(1.0f - roughness), F0) - F0) * pow;
//}
//vec3 SpecF_SchlickRoughnessApprox(in float VoH, in vec3 F0, in float roughness)
//{
//	//Spherical Gaussian Approximation
//	float pow = exp2((-5.55473f * VoH - 6.98316f) * VoH);
//	return F0 + (max(vec3(1.0f - roughness), F0) - F0) * pow;
//}
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
	float lightDist = length(L) / LightData.Brightness;
	float radius = LightData.Radius / LightData.Brightness;
	float attn = Attenuate(lightDist, radius);
	L = normalize(L);
	vec3 H = normalize(V + L);

	float NoL = max(dot(N, L), 0.0f);
	float NoH = max(dot(N, H), 0.0f);
	float NoV = max(dot(N, V), 0.0f);
	float HoV = max(dot(H, V), 0.0f);

	vec3 color = CalcColor(
		NoL, NoH, NoV, HoV,
		attn, albedo, rms, F0);

	float shadow = ReadPointShadowMap(radius, -L, lightDist, NoL);

	return color * shadow;
}
vec3 CalcTotalLight(
in vec3 fragPosWS,
in vec3 normal,
in vec3 albedo,
in vec3 rms)
{
	float metallic = rms.y;
	vec3 CameraPosition = vec3(InverseViewMatrix[3]);
	vec3 V = normalize(CameraPosition - fragPosWS);
	vec3 F0 = mix(vec3(0.04f), albedo, metallic);
  	return CalcPointLight(normal, V, fragPosWS, albedo, rms, F0);
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

	//Retrieve shading information from GBuffer textures
	vec3 albedo = texture(Texture0, uv).rgb;
	vec3 normal = texture(Texture1, uv).rgb;
	vec3 rms = texture(Texture2, uv).rgb;
	float depth = texture(Texture3, uv).r;

	//Resolve world fragment position using depth and screen UV
	vec3 fragPosWS = WorldPosFromDepth(depth, uv);

	//float fadeRange = MaxFade - MinFade;
	//float dist = length(CameraPosition - fragPosWS);
	//float strength = smoothstep(1.0f, 0.0f, clamp((dist - MinFade) / fadeRange, 0.0f, 1.0f));
	OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms);
}
