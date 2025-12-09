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

	vec3 kD = 1.0f - F;
	kD *= 1.0f - metallic;

	vec3 radiance = lightAttenuation * LightData.Color * LightData.DiffuseIntensity;
	return (kD * albedo / PI + spec) * radiance * NoL;
}
vec3 CalcTotalLight(
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
vec3 WorldPosFromDepth(in float depth, in vec2 uv)
{
	vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
	vec4 viewSpacePosition = InvProjMatrix * clipSpacePosition;
	viewSpacePosition /= viewSpacePosition.w;
	return (CameraToWorldSpaceMatrix * viewSpacePosition).xyz;
}
void main()
{
	vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);

	//Retrieve shading information from GBuffer textures
	vec3 albedo = texture(AlbedoOpacity, uv).rgb;
	vec3 normal = texture(Normal, uv).rgb;
	vec3 rms = texture(RMSE, uv).rgb;
	float depth = texture(DepthView, uv).r;

	//Resolve world fragment position using depth and screen UV
	vec3 fragPosWS = WorldPosFromDepth(depth, uv);

  float fadeRange = MaxFade - MinFade;
  float dist = length(CameraPosition - fragPosWS);
  float strength = smoothstep(1.0f, 0.0f, clamp((dist - MinFade) / fadeRange, 0.0f, 1.0f));
  OutColor = strength * CalcTotalLight(fragPosWS, normal, albedo, rms);
}
