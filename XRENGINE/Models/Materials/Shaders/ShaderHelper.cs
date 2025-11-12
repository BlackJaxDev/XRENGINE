
namespace XREngine.Rendering.Models.Materials
{
    public static class ShaderHelper
    {
        public static XRShader LoadEngineShader(string relativePath, EShaderType? type = null)
        {
            XRShader source = Engine.Assets.LoadEngineAsset<XRShader>("Shaders", relativePath);
            source._type = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
            //source.GenerateAsync = true;
            return source;
        }

        public static async Task<XRShader> LoadEngineShaderAsync(string relativePath, EShaderType? type = null)
        {
            XRShader source = await Engine.Assets.LoadEngineAssetAsync<XRShader>("Shaders", relativePath);
            source._type = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
            //source.GenerateAsync = true;
            return source;
        }

        public const string LightFalloff = "pow(clamp(1.0 - pow({1} / {0}, 4), 0.0, 1.0), 2.0) / ({1} * {1} + 1.0);";
        //public const string LightFallof = "clamp(1.0 - {1} * {1} / ({0} * {0}), 0.0, 1.0); attn *= attn;";
        //public const string LightFallof = "1.0f / (light.Attenuation[0] + light.Attenuation[1] * dist + light.Attenuation[2] * dist* dist)";
        public static string GetLightFalloff(string radiusName, string distanceName)
            => string.Format(LightFalloff, radiusName, distanceName);

        public static readonly string Frag_Nothing = @"
#version 100
void main() { }";
        /// <summary>
        /// Writes gl_FragCoord.z to out float Depth in layout location 0.
        /// </summary>
        public static readonly string Frag_DepthOutput = @"
#version 450
layout(location = 0) out float Depth;
void main()
{
    Depth = gl_FragCoord.z;
}";
        public static readonly string Func_WorldPosFromDepth = @"
Vector3 WorldPosFromDepth(float depth, Vector2 uv)
{
    float z = depth * 2.0 - 1.0;
    Vector4 clipSpacePosition = Vector4(uv * 2.0 - 1.0, z, 1.0);
    Vector4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
    viewSpacePosition /= viewSpacePosition.w;
    Vector4 worldSpacePosition = CameraToWorldSpaceMatrix * viewSpacePosition;
    return worldSpacePosition.xyz;
}";
        public static readonly string Func_ViewPosFromDepth = @"
Vector3 ViewPosFromDepth(float depth, Vector2 uv)
{
    float z = depth * 2.0 - 1.0;
    Vector4 clipSpacePosition = Vector4(uv * 2.0 - 1.0, z, 1.0);
    Vector4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}";
        public static readonly string Func_GetDistanceFromDepth = @"
float GetDistanceFromDepth(float depth)
{
    float depthSample = 2.0 * depth - 1.0;
    float zLinear = 2.0 * CameraNearZ * CameraFarZ / (CameraFarZ + CameraNearZ - depthSample * (CameraFarZ - CameraNearZ));
    return zLinear;
}";
        public static readonly string Func_GetDepthFromDistance = @"
float GetDepthFromDistance(float z)
{
    float nonLinearDepth = (CameraFarZ + CameraNearZ - 2.0 * CameraNearZ * CameraFarZ / z) / (CameraFarZ - CameraNearZ);
    nonLinearDepth = (nonLinearDepth + 1.0) / 2.0;
    return nonLinearDepth;
}";
        public static readonly string Func_RGBtoHSV = @"
Vector3 RGBtoHSV(Vector3 c)
{
    Vector4 K = Vector4(0.0, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
    Vector4 p = mix(Vector4(c.bg, K.wz), Vector4(c.gb, K.xy), step(c.b, c.g));
    Vector4 q = mix(Vector4(p.xyw, c.r), Vector4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10f;
    return Vector3(abs(q.z + (q.w - q.y) / (6.0f * d + e)), d / (q.x + e), q.x);
}";
        public static readonly string Func_HSVtoRGB = @"
Vector3 HSVtoRGB(Vector3 c)
{
    Vector4 K = Vector4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
    Vector3 p = abs(fract(c.xxx + K.xyz) * 6.0f - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0f, 1.0f), c.y);
}";
        
        public static XRShader LitTextureFragForward()
        {
            string source = @"
#version 450

layout (location = 0) out Vector4 OutColor;

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform Vector3 CameraPosition;

uniform sampler2D Texture0;

layout (location = 0) in Vector3 FragPos;
layout (location = 1) in Vector3 FragNorm;
layout (location = 6) in Vector2 FragUV0;

" + LightingDeclBasic() + @"

void main()
{
    Vector3 normal = normalize(FragNorm);
    Vector4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = 1.0;

    " + LightingCalcBasic("totalLight", "Vector3(0.0f)", "normal", "FragPos", "texColor.rgb", "MatSpecularIntensity", "AmbientOcclusion") + @"

    OutColor = texColor * Vector4(totalLight, 1.0);
}
";
            return new XRShader(EShaderType.Fragment, source);
        }
        public static XRShader? LitTextureFragDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedDeferred.fs"));
        public static XRShader? LitColorFragDeferred()
            => LoadEngineShader(Path.Combine("Common", "ColoredDeferred.fs"));
        public static XRShader LitTextureNormalFragDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedNormalDeferred.fs"));
        public static XRShader LitTextureNormalMetallicFragDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedNormalMetallicDeferred.fs"));
        public static XRShader LitTextureNormalRoughnessMetallicDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedNormalMetallicRoughnessDeferred.fs"));
        public static XRShader LitTextureMetallicFragDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedMetallicDeferred.fs"));
        public static XRShader LitTextureMetallicRoughnessDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedMetallicRoughnessDeferred.fs"));
        public static XRShader LitTextureRoughnessFragDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedRoughnessDeferred.fs"));
        public static XRShader LitTextureMatcapDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedMatcapDeferred.fs"));
        public static XRShader LitTextureEmissiveDeferred()
            => LoadEngineShader(Path.Combine("Common", "TexturedEmissiveDeferred.fs"));

        public static XRShader? UnlitTextureFragForward()
             => LoadEngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"));
        public static XRShader? UnlitTextureStereoFragForward()
             => LoadEngineShader(Path.Combine("Common", "UnlitTexturedStereoForward.fs"));
        public static XRShader? UnlitAlphaTextureFragForward()
            => LoadEngineShader(Path.Combine("Common", "UnlitAlphaTexturedForward.fs"));
        public static XRShader? UnlitColorFragForward()
             => LoadEngineShader(Path.Combine("Common", "UnlitColoredForward.fs"));

        public static XRShader? GaussianSplatVertex()
            => LoadEngineShader(Path.Combine("Gaussian", "GaussianSplat.vs"));

        public static XRShader? GaussianSplatFragment()
            => LoadEngineShader(Path.Combine("Gaussian", "GaussianSplat.fs"));

        public static XRShader LitColorFragForward()
        {
            string source = @"
#version 450

layout (location = 0) out Vector4 OutColor;

uniform Vector4 MatColor;
uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform Vector3 CameraPosition;
uniform Vector3 CameraForward;

layout (location = 0) in Vector3 FragPos;
layout (location = 1) in Vector3 FragNorm;

" + LightingDeclBasic() + @"

void main()
{
    Vector3 normal = normalize(FragNorm);

    " + LightingCalcForward() + @"

    OutColor = MatColor * Vector4(totalLight, 1.0);
}
";
            return new XRShader(EShaderType.Fragment, source);
        }

        public static string LightingCalcForward()
            => LightingCalcBasic("totalLight", "GlobalAmbient", "normal", "FragPos", "MatColor.rgb", "MatSpecularIntensity", "1.0");
        public static string LightingCalcBasic(
            string lightVarName,
            string baseLightVector3,
            string normalNameVector3,
            string fragPosNameVector3,
            string albedoNameRGB,
            string specNameIntensity,
            string ambientOcclusionFloat)
        {
            return string.Format(@"
    Vector3 {0} = {1};

    for (int i = 0; i < DirLightCount; ++i)
        {0} += CalcDirLight(DirectionalLights[i], {2}, {3}, {4}, {5}, {6});

    for (int i = 0; i < PointLightCount; ++i)
        {0} += CalcPointLight(PointLights[i], {2}, {3}, {4}, {5}, {6});

    for (int i = 0; i < SpotLightCount; ++i)
        {0} += CalcSpotLight(SpotLights[i], {2}, {3}, {4}, {5}, {6});",
        lightVarName, baseLightVector3, normalNameVector3, fragPosNameVector3, albedoNameRGB, specNameIntensity, ambientOcclusionFloat);
        }
        public static string LightingDeclBasic()
        {
            return @"

struct BaseLight
{
    Vector3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    mat4 WorldToLightSpaceProjMatrix;
    sampler2D ShadowMap;
};
struct DirLight
{
    BaseLight Base;
    Vector3 Direction;
};
struct PointLight
{
    BaseLight Base;
    Vector3 Position;
    float Radius;
    float Brightness;
};
struct SpotLight
{
    PointLight Base;
    Vector3 Direction;
    float InnerCutoff;
    float OuterCutoff;
    float Exponent;
};

uniform Vector3 GlobalAmbient;

uniform int DirLightCount; 
uniform DirLight DirectionalLights[2];

uniform int SpotLightCount;
uniform SpotLight SpotLights[16];

uniform int PointLightCount;
uniform PointLight PointLights[16];

//0 is fully in shadow, 1 is fully lit
float ReadShadowMap(in Vector3 fragPos, in Vector3 normal, in float diffuseFactor, in BaseLight light)
{
    float maxBias = 0.04;
    float minBias = 0.001;

    Vector4 fragPosLightSpace = light.WorldToLightSpaceProjMatrix * Vector4(fragPos, 1.0);
    Vector3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    fragCoord = fragCoord * Vector3(0.5) + Vector3(0.5);
    float bias = max(maxBias * -diffuseFactor, minBias);

    float depth = texture(light.ShadowMap, fragCoord.xy).r;
    float shadow = (fragCoord.z - bias) > depth ? 0.0 : 1.0;        

    //float shadow = 0.0;
    //Vector2 texelSize = 1.0 / textureSize(light.ShadowMap, 0);
    //for (int x = -1; x <= 1; ++x)
    //{
    //    for (int y = -1; y <= 1; ++y)
    //    {
    //        float pcfDepth = texture(light.ShadowMap, fragCoord.xy + Vector2(x, y) * texelSize).r;
    //        shadow += fragCoord.z - bias > pcfDepth ? 0.0 : 1.0;        
    //    }    
    //}
    //shadow *= 0.111111111; //divided by 9

    return shadow;
}

float Attenuate(in float dist, in float radius)
{
    return " + GetLightFalloff("radius", "dist") + @"
}

Vector3 CalcColor(BaseLight light, Vector3 lightDirection, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    Vector3 AmbientColor = Vector3(light.Color * light.AmbientIntensity);
    Vector3 DiffuseColor = Vector3(0.0);
    Vector3 SpecularColor = Vector3(0.0);

    float DiffuseFactor = dot(normal, -lightDirection);
    if (DiffuseFactor > 0.0)
    {
        DiffuseColor = light.Color * light.DiffuseIntensity * albedo * DiffuseFactor;

        Vector3 posToEye = normalize(CameraPosition - fragPos);
        Vector3 reflectDir = reflect(lightDirection, normal);
        float SpecularFactor = dot(posToEye, reflectDir);
        if (SpecularFactor > 0.0)
        {
            SpecularColor = light.Color * spec * pow(SpecularFactor, 64.0);
        }
    }

    float shadow = ReadShadowMap(fragPos, normal, DiffuseFactor, light);
    return (AmbientColor + (DiffuseColor + SpecularColor) * shadow) * ambientOcclusion;
}

Vector3 CalcDirLight(DirLight light, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    return CalcColor(light.Base, light.Direction, normal, fragPos, albedo, spec, ambientOcclusion);
}

Vector3 CalcPointLight(PointLight light, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    Vector3 lightToPos = fragPos - light.Position;
    return Attenuate(length(lightToPos), light.Radius) * CalcColor(light.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
} 

Vector3 CalcSpotLight(SpotLight light, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    //if (light.OuterCutoff <= 1.5707) //~90 degrees in radians
    {
        Vector3 lightToPos = normalize(fragPos - light.Base.Position);
        float clampedCosine = max(0.0, dot(lightToPos, normalize(light.Direction)));
        float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);
	    //if (clampedCosine >= light.OuterCutoff)
        {
            Vector3 lightToPos = fragPos - light.Base.Position;
            float spotAttn = pow(clampedCosine, light.Exponent);
            float distAttn = Attenuate(length(lightToPos) / light.Base.Brightness, light.Base.Radius);
            Vector3 color = CalcColor(light.Base.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
            return spotEffect * spotAttn * distAttn * color;
        }
    }
    return Vector3(0.0);
}
";
        }
        public static string LightingDeclPhysicallyBased()
        {
            return @"

struct BaseLight
{
    Vector3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    mat4 WorldToLightSpaceProjMatrix;
    sampler2D ShadowMap;
};
struct DirLight
{
    BaseLight Base;
    Vector3 Direction;
};
struct PointLight
{
    BaseLight Base;
    Vector3 Position;
    float Radius;
    float Brightness;
};
struct SpotLight
{
    PointLight Base;
    Vector3 Direction;
    float InnerCutoff;
    float OuterCutoff;
    float Exponent;
};

uniform Vector3 GlobalAmbient;

uniform int DirLightCount; 
uniform DirLight DirectionalLights[2];

uniform int SpotLightCount;
uniform SpotLight SpotLights[16];

uniform int PointLightCount;
uniform PointLight PointLights[16];

//0 is fully in shadow, 1 is fully lit
float ReadShadowMap(in Vector3 fragPos, in Vector3 normal, in float diffuseFactor, in BaseLight light)
{
    float maxBias = 0.04;
    float minBias = 0.001;

    Vector4 fragPosLightSpace = light.WorldToLightSpaceProjMatrix * Vector4(fragPos, 1.0);
    Vector3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    fragCoord = fragCoord * Vector3(0.5) + Vector3(0.5);
    float bias = max(maxBias * -diffuseFactor, minBias);

    float depth = texture(light.ShadowMap, fragCoord.xy).r;
    float shadow = fragCoord.z - bias > depth ? 0.0 : 1.0;        

    //float shadow = 0.0;
    //Vector2 texelSize = 1.0 / textureSize(light.ShadowMap, 0);
    //for (int x = -1; x <= 1; ++x)
    //{
    //    for (int y = -1; y <= 1; ++y)
    //    {
    //        float pcfDepth = texture(light.ShadowMap, fragCoord.xy + Vector2(x, y) * texelSize).r;
    //        shadow += fragCoord.z - bias > pcfDepth ? 0.0 : 1.0;        
    //    }    
    //}
    //shadow *= 0.111111111; //divided by 9

    return shadow;
}

float Attenuate(in float dist, in float radius)
{
    return " + GetLightFalloff("radius", "dist") + @"
}

Vector3 CalcColor(BaseLight light, Vector3 lightDirection, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    Vector3 AmbientColor = Vector3(light.Color * light.AmbientIntensity);
    Vector3 DiffuseColor = Vector3(0.0);
    Vector3 SpecularColor = Vector3(0.0);

    float DiffuseFactor = dot(normal, -lightDirection);
    if (DiffuseFactor > 0.0)
    {
        DiffuseColor = light.Color * light.DiffuseIntensity * albedo * DiffuseFactor;

        Vector3 posToEye = normalize(CameraPosition - fragPos);
        Vector3 reflectDir = reflect(lightDirection, normal);
        float SpecularFactor = dot(posToEye, reflectDir);
        if (SpecularFactor > 0.0)
        {
            SpecularColor = light.Color * spec * pow(SpecularFactor, 64.0);
        }
    }

    float shadow = ReadShadowMap(fragPos, normal, DiffuseFactor, light);
    return (AmbientColor + (DiffuseColor + SpecularColor) * shadow) * ambientOcclusion;
}

Vector3 CalcDirLight(DirLight light, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    return CalcColor(light.Base, light.Direction, normal, fragPos, albedo, spec, ambientOcclusion);
}

Vector3 CalcPointLight(PointLight light, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    Vector3 lightToPos = fragPos - light.Position;
    return Attenuate(length(lightToPos), light.Radius) * CalcColor(light.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
} 

Vector3 CalcSpotLight(SpotLight light, Vector3 normal, Vector3 fragPos, Vector3 albedo, float spec, float ambientOcclusion)
{
    //if (light.OuterCutoff <= 1.5707) //~90 degrees in radians
    {
        Vector3 lightToPos = normalize(fragPos - light.Base.Position);
        float clampedCosine = max(0.0, dot(lightToPos, normalize(light.Direction)));
        float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);
	    //if (clampedCosine >= light.OuterCutoff)
        {
            Vector3 lightToPos = fragPos - light.Base.Position;
            float spotAttn = pow(clampedCosine, light.Exponent);
            float distAttn = Attenuate(length(lightToPos) / light.Base.Brightness, light.Base.Radius);
            Vector3 color = CalcColor(light.Base.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
            return spotEffect * spotAttn * distAttn * color;
        }
    }
    return Vector3(0.0);
}
";
        }
        public static XRShader LightingSetupPhysicallyBased()
        {
            string source = @"
const float PI = 3.14159265359;
const float InvPI = 0.31831;

//Trowbridge-Reitz GGX
float SpecD_TRGGX(float NoH2, float a2)
{
    float num    = a2;
    float denom  = (NoH2 * (a2 - 1.0) + 1.0);
    denom        = PI * denom * denom;

    return num / denom;
}
float SpecG_SchlickGGX(float NdotV, float k)
{
    float num   = NdotV;
   	float denom = NdotV * (1.0 - k) + k;

    return num / denom;
}
float SpecG_Smith(float NoV, float NoL, float k)
{
    float ggx1 = SpecG_SchlickGGX(NoV, k);
    float ggx2 = SpecG_SchlickGGX(NoL, k);
    return ggx1 * ggx2;
}
Vector3 SpecF_Schlick(float VoH, Vector3 F0)
{
	//Regular implementation
	//float pow = pow(1.0 - VoH, 5.0);

	//Spherical Gaussian Approximation
	//https://seblagarde.wordpress.com/2012/06/03/spherical-gaussien-approximation-for-blinn-phong-phong-and-fresnel/
	float pow = exp2((-5.55473 * VoH - 6.98316) * VoH);

    return F0 + (1.0 - F0) * pow;
}
Vector3 Spec_CookTorrance(float D, float G, Vector3 F, float NoV, float NoL)
{
	Vector3 num = D * G * F;
	float denom = 4.0 * NoV * NoL + 0.001; 
	return num / denom;
}
Vector3 CalcLighting(Vector3 N, Vector3 L, Vector3 color, float roughness, float metallic, float ior)
{
	float NoV = max(dot(N, V), 0.0);
	float NoL = max(dot(N, L), 0.0);
	float NoH = max(dot(N, H), 0.0);
	float NoH2 = NoH * NoH;

	float a = roughness * roughness;
	float a2 = a * a;

	float k = pow(roughness + 1.0, 2.0) * 0.125; //divide by 8

	float D = SpecD_TRGGX(NoH2, a2);
	float G = SpecG_Smith(NoV, NoL, k);
	float F = SpecF_Schlick(VoH, F0);

	float NdotH  = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

	Vector3 F0 = abs((1.0 - ior) / (1.0 + ior));
	F0      = mix(F0, color, metallic);
 
	Vector3 kS = F;
	Vector3 kD = (Vector3(1.0) - kS) * (1.0 - metallic);

	Vector3 specular = Spec_CookTorrance(D, G, F, NoV, NoL);
	return kd * color * InvPI + ks * specular;
      
    Lo += CalcLighting() * radiance * NoL;
}

float RiemannSum()
{
    int steps = 100;
    float sum = 0.0f;
    Vector3 P    = ...;
    Vector3 Wo   = ...;
    Vector3 N    = ...;
    float dW  = 1.0f / steps;
    for(int i = 0; i < steps; ++i) 
    {
        Vector3 Wi = getNextIncomingLightDir(i);
        sum += Fr(P, Wi, Wo) * L(P, Wi) * dot(N, Wi) * dW;
    }
}

Vector3 ImportanceSampleGGX(Vector2 Xi, float a, Vector3 N)
{
	float Phi = 2.0 * PI * Xi.x;

	float CosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
	float SinTheta = sqrt(1.0 - CosTheta * CosTheta);

	Vector3 H = Vector3(SinTheta * cos(Phi), SinTheta * sin(Phi), CosTheta);
	Vector3 UpVector = abs(N.z) < 0.999 ? Vector3(0.0, 0.0, 1.0) : Vector3(1.0, 0.0, 0.0);
	Vector3 TangentX = normalize(cross(UpVector, N));
	Vector3 TangentY = cross(N, TangentX);

	// Tangent to world space
	return TangentX * H.x + TangentY * H.y + N * H.z;
}
Vector3 SpecularIBL(Vector3 SpecularColor, float Roughness, Vector3 N, Vector3 V)
{
	const uint NumSamples = 1024;

	Vector3 radiance = Vector3(0.0);
	float a = Roughness * Roughness;
	for (uint i = 0; i < NumSamples; i++)
	{
		Vector2 Xi = Hammersley(i, NumSamples);
		Vector3 H = ImportanceSampleGGX(Xi, a, N);
		float VoH = dot(V, H);
		Vector3 L = Vector3(2.0 * VoH) * H - V;
		float NoV = saturate(dot(N, V));
		float NoL = saturate(dot(N, L));
		float NoH = saturate(dot(N, H));
		VoH = saturate(VoH);
		if(NoL > 0)
		{
			Vector3 SampleColor = EnvMap.SampleLevel(EnvMapSampler, L, 0).rgb;
			float G = G_Smith(Roughness, NoV, NoL);
			float Fc = pow( 1 - VoH, 5 );
			float3 F = (1 - Fc) * SpecularColor + Fc;
			// Incident light = SampleColor * NoL
			// Microfacet specular = D*G*F / (4*NoL*NoV)
			// pdf = D * NoH / (4 * VoH)
			radiance += SampleColor * F * G * VoH / (NoH * NoV);
		}
	}
	return radiance / NumSamples;
}

in Vector2 v_texcoord; // texture coords
in Vector3 v_normal;   // normal
in Vector3 v_binormal; // binormal (for TBN basis calc)
in Vector3 v_pos;      // pixel view space position

out Vector4 color;

layout(std140) uniform Material
{
    Vector4 material; // x - metallic, y - roughness, w - 'rim' lighting
    Vector4 albedo;   // constant albedo color, used when textures are off
};

uniform samplerCube envd;  // prefiltered env cubemap
uniform sampler2D tex;     // base texture (albedo)
uniform sampler2D norm;    // normal map
uniform sampler2D spec;    // 'factors' texture (G channel used as roughness)
uniform sampler2D iblbrdf; // IBL BRDF normalization precalculated tex

#define PI 3.1415926

// constant light position, only one light source for testing (treated as point light)
const Vector4 light_pos = Vector4(-2, 3, -2, 1);

// handy value clamping to 0 - 1 range
float saturate(in float value)
{
    return clamp(value, 0.0, 1.0);
}

// phong (lambertian) diffuse term
float phong_diffuse()
{
    return (1.0 / PI);
}

// compute fresnel specular factor for given base specular and product
// product could be NdV or VdH depending on used technique
Vector3 fresnel_factor(in Vector3 f0, in float product)
{
    return mix(f0, Vector3(1.0), pow(1.01 - product, 5.0));
}

// following functions are copies of UE4
// for computing cook-torrance specular lighting terms

float D_blinn(in float roughness, in float NdH)
{
    float m = roughness * roughness;
    float m2 = m * m;
    float n = 2.0 / m2 - 2.0;
    return (n + 2.0) / (2.0 * PI) * pow(NdH, n);
}

float D_beckmann(in float roughness, in float NdH)
{
    float m = roughness * roughness;
    float m2 = m * m;
    float NdH2 = NdH * NdH;
    return exp((NdH2 - 1.0) / (m2 * NdH2)) / (PI * m2 * NdH2 * NdH2);
}

float D_GGX(in float roughness, in float NdH)
{
    float m = roughness * roughness;
    float m2 = m * m;
    float d = (NdH * m2 - NdH) * NdH + 1.0;
    return m2 / (PI * d * d);
}

float G_schlick(in float roughness, in float NdV, in float NdL)
{
    float k = roughness * roughness * 0.5;
    float V = NdV * (1.0 - k) + k;
    float L = NdL * (1.0 - k) + k;
    return 0.25 / (V * L);
}

// simple phong specular calculation with normalization
Vector3 phong_specular(in Vector3 V, in Vector3 L, in Vector3 N, in Vector3 specular, in float roughness)
{
    Vector3 R = reflect(-L, N);
    float spec = max(0.0, dot(V, R));

    float k = 1.999 / (roughness * roughness);

    return min(1.0, 3.0 * 0.0398 * k) * pow(spec, min(10000.0, k)) * specular;
}

// simple blinn specular calculation with normalization
Vector3 blinn_specular(in float NdH, in Vector3 specular, in float roughness)
{
    float k = 1.999 / (roughness * roughness);
    return min(1.0, 3.0 * 0.0398 * k) * pow(NdH, min(10000.0, k)) * specular;
}

// cook-torrance specular calculation                      
Vector3 cooktorrance_specular(in float NdL, in float NdV, in float NdH, in Vector3 specular, in float roughness)
{
#ifdef COOK_BLINN
    float D = D_blinn(roughness, NdH);
#endif

#ifdef COOK_BECKMANN
    float D = D_beckmann(roughness, NdH);
#endif

#ifdef COOK_GGX
    float D = D_GGX(roughness, NdH);
#endif

    float G = G_schlick(roughness, NdV, NdL);
    float rim = mix(1.0 - roughness * material.w * 0.9, 1.0, NdV);
    return (1.0 / rim) * specular * G * D;
}

void main()
{
    // point light direction to point in view space
    Vector3 local_light_pos = (view_matrix * (/*world_matrix */ light_pos)).xyz;

    // light attenuation
    float A = 20.0 / dot(local_light_pos - v_pos, local_light_pos - v_pos);

    // L, V, H vectors
    Vector3 L = normalize(local_light_pos - v_pos);
    Vector3 V = normalize(-v_pos);
    Vector3 H = normalize(L + V);
    Vector3 nn = normalize(v_normal);

    Vector3 nb = normalize(v_binormal);
    mat3x3 tbn = mat3x3(nb, cross(nn, nb), nn);


    Vector2 texcoord = v_texcoord;


    // normal map
#if USE_NORMAL_MAP
    // tbn basis
    Vector3 N = tbn * (texture2D(norm, texcoord).xyz * 2.0 - 1.0);
#else
    Vector3 N = nn;
#endif

    // albedo/specular base
#if USE_ALBEDO_MAP
    Vector3 base = texture2D(tex, texcoord).xyz;
#else
    Vector3 base = albedo.xyz;
#endif

    // roughness
#if USE_ROUGHNESS_MAP
    float roughness = texture2D(spec, texcoord).y * material.y;
#else
    float roughness = material.y;
#endif

    // material params
    float metallic = material.x;

    // mix between metal and non-metal material, for non-metal
    // constant base specular factor of 0.04 grey is used
    Vector3 specular = mix(Vector3(0.04), base, metallic);

    // diffuse IBL term
    //    I know that my IBL cubemap has diffuse pre-integrated value in 10th MIP level
    //    actually level selection should be tweakable or from separate diffuse cubemap
    mat3x3 tnrm = transpose(normal_matrix);
    Vector3 envdiff = textureCubeLod(envd, tnrm * N, 10).xyz;

    // specular IBL term
    //    11 magic number is total MIP levels in cubemap, this is simplest way for picking
    //    MIP level from roughness value (but it's not correct, however it looks fine)
    Vector3 refl = tnrm * reflect(-V, N);
    Vector3 envspec = textureCubeLod(envd, refl, max(roughness * 11.0, textureQueryLod(envd, refl).y)).xyz;

    // compute material reflectance
    float NdL = max(0.0, dot(N, L));
    float NdV = max(0.001, dot(N, V));
    float NdH = max(0.001, dot(N, H));
    float HdV = max(0.001, dot(H, V));
    float LdV = max(0.001, dot(L, V));

    // fresnel term is common for any, except phong
    // so it will be calcuated inside ifdefs

# ifdef PHONG
    // specular reflectance with PHONG
    Vector3 specfresnel = fresnel_factor(specular, NdV);
    Vector3 specref = phong_specular(V, L, N, specfresnel, roughness);
#endif

# ifdef BLINN
    // specular reflectance with BLINN
    Vector3 specfresnel = fresnel_factor(specular, HdV);
    Vector3 specref = blinn_specular(NdH, specfresnel, roughness);
#endif

# ifdef COOK
    // specular reflectance with COOK-TORRANCE
    Vector3 specfresnel = fresnel_factor(specular, HdV);
    Vector3 specref = cooktorrance_specular(NdL, NdV, NdH, specfresnel, roughness);
#endif

    specref *= Vector3(NdL);

    // diffuse is common for any model
    Vector3 diffref = (Vector3(1.0) - specfresnel) * phong_diffuse() * NdL;

    // compute lighting
    Vector3 reflected_light = Vector3(0);
    Vector3 diffuse_light = Vector3(0); // initial value == constant ambient light

    // point light
    Vector3 light_color = Vector3(1.0) * A;
    reflected_light += specref * light_color;
    diffuse_light += diffref * light_color;

    // IBL lighting
    Vector2 brdf = texture2D(iblbrdf, Vector2(roughness, 1.0 - NdV)).xy;
    Vector3 iblspec = min(Vector3(0.99), fresnel_factor(specular, NdV) * brdf.x + brdf.y);
    reflected_light += iblspec * envspec;
    diffuse_light += envdiff * (1.0 / PI);

    // final result
    Vector3 result =
    diffuse_light * mix(base, Vector3(0.0), metallic) +
    reflected_light;

    color = Vector4(result, 1);
}
";
            return new XRShader(EShaderType.Fragment, source);
        }
    }
}
