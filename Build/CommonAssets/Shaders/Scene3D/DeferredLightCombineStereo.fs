#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

#pragma snippet "NormalEncoding"
#pragma snippet "OctahedralMapping"
#pragma snippet "PBRFunctions"
#pragma snippet "DepthUtils"

const float MAX_REFLECTION_LOD = 4.0f;

layout(location = 0) out vec4 OutLo; //Diffuse Light Color, to start off the HDR Scene Texture
layout(location = 0) in vec3 FragPos;

layout(binding = 0) uniform sampler2DArray AlbedoOpacity;
layout(binding = 1) uniform sampler2DArray Normal;
layout(binding = 2) uniform sampler2DArray RMSE;
layout(binding = 3) uniform sampler2DArray AmbientOcclusionTexture;
layout(binding = 4) uniform sampler2DArray DepthView;
layout(binding = 5) uniform sampler2DArray LightingTexture;

layout(binding = 6) uniform sampler2D BRDF;

layout(binding = 7) uniform sampler2D Irradiance;
layout(binding = 8) uniform sampler2D Prefilter;
uniform bool UseAmbientOcclusion = true;
uniform float AmbientOcclusionPower = 1.0f;
uniform bool AmbientOcclusionMultiBounce = false;
uniform bool SpecularOcclusionEnabled = false;

vec3 MultiBounceAO(float ao, vec3 albedo)
{
    vec3 a = 2.0404f * albedo - 0.3324f;
    vec3 b = -4.7951f * albedo + 0.6417f;
    vec3 c = 2.7552f * albedo + 0.6903f;
    return max(vec3(ao), ((a * ao + b) * ao + c) * ao);
}

float GTSpecularOcclusion(float NoV, float ao, float roughness)
{
    return clamp(pow(NoV + ao, exp2(-16.0f * roughness - 1.0f)) - 1.0f + ao, 0.0f, 1.0f);
}

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;

uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;
void main()
{
	vec2 uv = FragPos.xy;
	if (uv.x > 1.0f || uv.y > 1.0f)
		discard;
	//Normalize uv from [-1, 1] to [0, 1]
	uv = uv * 0.5f + 0.5f;
	vec3 uvi = vec3(uv, gl_ViewID_OVR);
	bool leftEye = gl_ViewID_OVR == 0;
	mat4 InverseViewMatrix = leftEye ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
	mat4 ProjMatrix = leftEye ? LeftEyeProjMatrix : RightEyeProjMatrix;

	vec3 albedoColor = texture(AlbedoOpacity, uvi).rgb;
	vec3 normal = XRENGINE_ReadNormal(Normal, uvi);
	vec3 rms = texture(RMSE, uvi).rgb;
	float ao = UseAmbientOcclusion ? pow(texture(AmbientOcclusionTexture, uvi).r, max(AmbientOcclusionPower, 0.001f)) : 1.0f;
	float depth = texture(DepthView, uvi).r;
	vec3 InLo = texture(LightingTexture, uvi).rgb;
	vec3 irradianceColor = XRENGINE_SampleOcta(Irradiance, normal);
	vec3 fragPosWS = XRENGINE_WorldPosFromDepthRaw(depth, uv, inverse(ProjMatrix), InverseViewMatrix);
	//float fogDensity = noise3(fragPosWS);

	float roughness = rms.x;
  	float metallic = rms.y;
	float specularIntensity = rms.z;

	vec3 CameraPosition = vec3(InverseViewMatrix[3]);
	vec3 V = normalize(CameraPosition - fragPosWS);
	float NoV = max(dot(normal, V), 0.0f);
	vec3 F0 = mix(vec3(0.04f), albedoColor, metallic);
	vec2 brdfValue = texture(BRDF, vec2(NoV, roughness)).rg;

	//Calculate specular and diffuse components
	//Preserve energy by making sure they add up to 1
	vec3 kS = XRENGINE_F_SchlickRoughnessFast(NoV, F0, roughness) * specularIntensity;
	vec3 kD = (1.0f - kS) * (1.0f - metallic);
	vec3 R = reflect(-V, normal);

	//TODO: fix reflection vector, blend environment cubemaps via influence radius

	vec3 diffuse = irradianceColor * albedoColor;
	vec3 prefilteredColor = XRENGINE_SampleOctaLod(Prefilter, R, roughness * MAX_REFLECTION_LOD);
	vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);

	vec3 diffuseAO = AmbientOcclusionMultiBounce ? MultiBounceAO(ao, albedoColor) : vec3(ao);
	float specOcclusion = SpecularOcclusionEnabled ? GTSpecularOcclusion(NoV, ao, roughness) : ao;
	vec3 ambient = kD * diffuse * diffuseAO + specular * specOcclusion;

	OutLo = vec4(ambient + InLo, 1.0);
}
