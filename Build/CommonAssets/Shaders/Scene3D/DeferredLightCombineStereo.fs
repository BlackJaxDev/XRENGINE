#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;
const float MAX_REFLECTION_LOD = 4.0f;

layout(location = 0) out vec3 OutLo; //Diffuse Light Color, to start off the HDR Scene Texture
layout(location = 0) in vec3 FragPos;

layout(binding = 0) uniform sampler2DArray Texture0; //AlbedoOpacity
layout(binding = 1) uniform sampler2DArray Texture1; //Normal
layout(binding = 2) uniform sampler2DArray Texture2; //PBR: Roughness, Metallic, Specular, Index of refraction
layout(binding = 3) uniform sampler2DArray Texture3; //SSAO Intensity
layout(binding = 4) uniform sampler2DArray Texture4; //Depth
layout(binding = 5) uniform sampler2DArray Texture5; //Diffuse Light Color

layout(binding = 6) uniform sampler2D BRDF;

layout(binding = 7) uniform sampler2D Irradiance;
layout(binding = 8) uniform sampler2D Prefilter;
vec2 EncodeOcta(vec3 dir)
{
	dir = normalize(dir);
	dir /= max(abs(dir.x) + abs(dir.y) + abs(dir.z), 1e-5f);

	vec2 uv = dir.xy;
	if (dir.z < 0.0f)
	{
		vec2 signDir = vec2(dir.x >= 0.0f ? 1.0f : -1.0f, dir.y >= 0.0f ? 1.0f : -1.0f);
		uv = (1.0f - abs(uv.yx)) * signDir;
	}

	return uv * 0.5f + 0.5f;
}

vec3 SampleOcta(sampler2D tex, vec3 dir)
{
	vec2 uv = EncodeOcta(dir);
	return texture(tex, uv).rgb;
}

vec3 SampleOctaLod(sampler2D tex, vec3 dir, float lod)
{
	vec2 uv = EncodeOcta(dir);
	return textureLod(tex, uv, lod).rgb;
}


uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;

uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;

vec3 SpecF_SchlickRoughness(in float VoH, in vec3 F0, in float roughness)
{
	float pow = pow(1.0f - VoH, 5.0f);
	return F0 + (max(vec3(1.0f - roughness), F0) - F0) * pow;
}
vec3 SpecF_SchlickRoughnessApprox(in float VoH, in vec3 F0, in float roughness)
{
	//Spherical Gaussian Approximation
	float pow = exp2((-5.55473f * VoH - 6.98316f) * VoH);
	return F0 + (max(vec3(1.0f - roughness), F0) - F0) * pow;
}
vec3 WorldPosFromDepth(in float depth, in vec2 uv, in mat4 InverseViewMatrix, in mat4 ProjMatrix)
{
	vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
	vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
	viewSpacePosition /= viewSpacePosition.w;
	return (InverseViewMatrix * viewSpacePosition).xyz;
}
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

	vec3 albedoColor = texture(Texture0, uvi).rgb;
	vec3 normal = texture(Texture1, uvi).rgb;
	vec3 rms = texture(Texture2, uvi).rgb;
	float ao = texture(Texture3, uvi).r;
	float depth = texture(Texture4, uvi).r;
	vec3 InLo = texture(Texture5, uvi).rgb;
	vec3 irradianceColor = SampleOcta(Irradiance, normal);
	vec3 fragPosWS = WorldPosFromDepth(depth, uv, InverseViewMatrix, ProjMatrix);
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
	vec3 kS = SpecF_SchlickRoughnessApprox(NoV, F0, roughness) * specularIntensity;
	vec3 kD = (1.0f - kS) * (1.0f - metallic);
	vec3 R = reflect(-V, normal);

	//TODO: fix reflection vector, blend environment cubemaps via influence radius

	vec3 diffuse = irradianceColor * albedoColor;
	vec3 prefilteredColor = SampleOctaLod(Prefilter, R, roughness * MAX_REFLECTION_LOD);
	vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
	vec3 ambient = (kD * diffuse + specular) * ao;

	OutLo = ambient + InLo;
}
