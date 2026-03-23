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
	vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);
	if (!XRENGINE_ShadowCoordInBounds(fragCoord))
 return 1.0f;
	//Create bias depending on angle of normal to the light
	float bias = GetShadowBias(NoL);

	float depth = texture(ShadowMap, fragCoord.xy).r;
	float litHard = XRENGINE_SampleShadowMapSimpleAddBias(ShadowMap, fragCoord, bias);
	float lit = XRENGINE_SampleShadowMapPCFAddBias(ShadowMap, fragCoord, bias, 3);
	return XRENGINE_BlendShadowFilter(litHard, lit, fragCoord.z - depth, 0.1f);
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
