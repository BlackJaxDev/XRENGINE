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
uniform float ShadowBase = 0.035f;
uniform float ShadowMult = 1.221f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;
uniform bool LightHasShadowMap = true; // Added
uniform int ShadowSamples = 4;
uniform float ShadowFilterRadius = 0.0012f;
uniform bool EnablePCSS = true;
// Debug: 0=normal, 1=shadow-only (white=lit), 2=margin heatmap (green=lit, red=shadow)
uniform int ShadowDebugMode = 0;

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

float SampleShadowCubePCFLocal(in samplerCube shadowMap, in vec3 shadowDir, in float biasedLightDist, in float farPlaneDist, in float sampleRadius)
{
	float lit = 0.0f;

	for (int i = 0; i < 20; ++i)
	{
		vec3 sampleDir = normalize(shadowDir + LocalShadowCubeKernel[i] * sampleRadius);
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

float SampleShadowCubeFilteredLocal(in samplerCube shadowMap, in vec3 shadowDir, in float biasedLightDist, in float farPlaneDist, in float sampleRadius, in int requestedSamples, in bool enableSoft)
{
	int sampleCount = clamp(requestedSamples, 1, 20);
	if (sampleCount <= 1)
	{
		float sampleDepth = texture(shadowMap, normalize(shadowDir)).r * farPlaneDist;
		return biasedLightDist <= sampleDepth ? 1.0f : 0.0f;
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
			lit += biasedLightDist <= sampleDepth ? 1.0f : 0.0f;
		}

		return lit / float(sampleCount);
	}

	return SampleShadowCubePCFLocal(shadowMap, shadowDir, biasedLightDist, farPlaneDist, sampleRadius);
}

// Global debug state written by ReadPointShadowMap for visualization
float _dbgShadowLit = 1.0f;
float _dbgShadowMargin = 1.0f;

// returns 1 lit, 0 shadow
float ReadPointShadowMap(in float farPlaneDist, in vec3 fragPosWS, in vec3 N, in float NoL)
{
	if (!LightHasShadowMap) return 1.0f;

	vec3 fragToLight = fragPosWS - LightData.Position;
	float lightDist = length(fragToLight);

	// Beyond the shadow far plane: treat as lit (attenuation handles falloff)
	if (lightDist >= farPlaneDist) return 1.0f;

	float faceSize = max(float(textureSize(ShadowMap, 0).x), 1.0f);

	// ---- Cubemap texel bias (relative) ----
	float slopeFactor = 1.0f - NoL;
	float texelRel = (2.0f / faceSize) * (1.0f + 3.0f * slopeFactor * slopeFactor);

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

	// ---- User bias (converted to relative) ----
	float userBias = GetShadowBias(NoL);
	float userRel  = userBias / max(lightDist, 0.001f);

	// Take the largest of the three contributors
	float relThreshold = max(texelRel, max(userRel, depthRel));

	// Pre-multiply threshold into lightDist for a single comparison per sample
	float biasedLightDist = lightDist * (1.0f - relThreshold);

	float sampleRadius = GetShadowCubeSampleRadius(ShadowMap, ShadowFilterRadius);

	float lit = SampleShadowCubeFilteredLocal(
		ShadowMap,
		fragToLight,
		biasedLightDist,
		farPlaneDist,
		sampleRadius,
		ShadowSamples,
		EnablePCSS);

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

	OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms);

	// Debug visualisation (additive blending still applies; works best with a single point light)
	if (ShadowDebugMode == 1)
	{
		// Shadow-only: white = fully lit, black = fully shadowed
		OutColor = vec3(_dbgShadowLit);
	}
	else if (ShadowDebugMode == 2)
	{
		// Margin heatmap: green = lit margin, red = shadow margin, brighter = more margin
		float m = _dbgShadowMargin;
		OutColor = m >= 0.0f ? vec3(0.0f, min(m * 20.0f, 1.0f), 0.0f)
		                     : vec3(min(-m * 20.0f, 1.0f), 0.0f, 0.0f);
	}
}
