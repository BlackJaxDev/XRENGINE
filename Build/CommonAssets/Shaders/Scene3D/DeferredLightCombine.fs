#version 450

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;
const float MAX_REFLECTION_LOD = 4.0f;

layout(location = 0) out vec3 OutLo; //Diffuse Light Color, to start off the HDR Scene Texture
layout(location = 0) in vec3 FragPos;

layout(binding = 0) uniform sampler2D Texture0; //AlbedoOpacity
layout(binding = 1) uniform sampler2D Texture1; //Normal
layout(binding = 2) uniform sampler2D Texture2; //PBR: Roughness, Metallic, Specular, Index of refraction
layout(binding = 3) uniform sampler2D Texture3; //SSAO Intensity
layout(binding = 4) uniform sampler2D Texture4; //Depth
layout(binding = 5) uniform sampler2D Texture5; //Diffuse Light Color

layout(binding = 6) uniform sampler2D BRDF;

layout(binding = 7) uniform sampler2DArray IrradianceArray;
layout(binding = 8) uniform sampler2DArray PrefilterArray;

layout(std430, binding = 0) buffer LightProbePositions
{
        vec4 ProbePositions[];
};

layout(std430, binding = 1) buffer LightProbeTetrahedra
{
        vec4 TetraIndices[];
};

uniform int ProbeCount;
uniform int TetraCount;
vec2 EncodeOcta(vec3 dir)
{
	dir = normalize(dir);
	// Swizzle: world (x,y,z) -> octahedral (x,z,y)
	// So world Y (up) -> octahedral Z (center/corners discriminator)
	vec3 octDir = vec3(dir.x, dir.z, dir.y);
	octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5f);

	vec2 uv = octDir.xy;
	if (octDir.z < 0.0f)
	{
		vec2 signDir = vec2(octDir.x >= 0.0f ? 1.0f : -1.0f, octDir.y >= 0.0f ? 1.0f : -1.0f);
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

vec3 SampleOctaArray(sampler2DArray tex, vec3 dir, float layer)
{
        vec2 uv = EncodeOcta(dir);
        return texture(tex, vec3(uv, layer)).rgb;
}

vec3 SampleOctaArrayLod(sampler2DArray tex, vec3 dir, float layer, float lod)
{
        vec2 uv = EncodeOcta(dir);
        return textureLod(tex, vec3(uv, layer), lod).rgb;
}


uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

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
vec3 WorldPosFromDepth(in float depth, in vec2 uv)
{
        vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
        vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
        viewSpacePosition /= viewSpacePosition.w;
        return (InverseViewMatrix * viewSpacePosition).xyz;
}

bool ComputeBarycentric(vec3 p, vec3 a, vec3 b, vec3 c, vec3 d, out vec4 bary)
{
        mat3 m = mat3(b - a, c - a, d - a);
        vec3 v = p - a;
        vec3 uvw = inverse(m) * v;
        float w = 1.0f - uvw.x - uvw.y - uvw.z;
        bary = vec4(uvw, w);
        return bary.x >= -0.0001f && bary.y >= -0.0001f && bary.z >= -0.0001f && bary.w >= -0.0001f;
}

void ResolveProbeWeights(vec3 worldPos, out vec4 weights, out ivec4 indices)
{
        weights = vec4(0.0f);
        indices = ivec4(-1);

        for (int i = 0; i < TetraCount; ++i)
        {
                ivec4 idx = ivec4(TetraIndices[i]);
                if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
                        continue;

                vec4 bary;
                vec3 a = ProbePositions[idx.x].xyz;
                vec3 b = ProbePositions[idx.y].xyz;
                vec3 c = ProbePositions[idx.z].xyz;
                vec3 d = ProbePositions[idx.w].xyz;

                if (ComputeBarycentric(worldPos, a, b, c, d, bary))
                {
                        weights = bary;
                        indices = idx;
                        return;
                }
        }

        float bestDistances[4];
        int bestIndices[4];
        for (int k = 0; k < 4; ++k)
        {
                bestDistances[k] = 1e20f;
                bestIndices[k] = -1;
        }

        for (int i = 0; i < ProbeCount; ++i)
        {
                float d = length(worldPos - ProbePositions[i].xyz);
                for (int k = 0; k < 4; ++k)
                {
                        if (d < bestDistances[k])
                        {
                                for (int s = 3; s > k; --s)
                                {
                                        bestDistances[s] = bestDistances[s - 1];
                                        bestIndices[s] = bestIndices[s - 1];
                                }
                                bestDistances[k] = d;
                                bestIndices[k] = i;
                                break;
                        }
                }
        }

        float sum = 0.0f;
        for (int k = 0; k < 4; ++k)
        {
                if (bestIndices[k] >= 0)
                {
                        float w = 1.0f / max(bestDistances[k], 0.0001f);
                        weights[k] = w;
                        indices[k] = bestIndices[k];
                        sum += w;
                }
        }
        if (sum > 0.0f)
                weights /= sum;
}
void main()
{
        vec2 uv = FragPos.xy;
        if (uv.x > 1.0f || uv.y > 1.0f)
                discard;
	//Normalize uv from [-1, 1] to [0, 1]
	uv = uv * 0.5f + 0.5f;

	vec3 albedoColor = texture(Texture0, uv).rgb;
	vec3 normal = texture(Texture1, uv).rgb;
	vec4 rmse = texture(Texture2, uv);
	float ao = texture(Texture3, uv).r;
	float depth = texture(Texture4, uv).r;
	vec3 InLo = max(texture(Texture5, uv).rgb, vec3(0.0f));
        vec3 fragPosWS = WorldPosFromDepth(depth, uv);
        //float fogDensity = noise3(fragPosWS);

        vec4 probeWeights;
        ivec4 probeIndices;
        ResolveProbeWeights(fragPosWS, probeWeights, probeIndices);

        vec3 irradianceColor = vec3(0.0f);
        vec3 prefilteredColor = vec3(0.0f);

        for (int i = 0; i < 4; ++i)
        {
                if (probeIndices[i] < 0)
                        continue;
                irradianceColor += probeWeights[i] * SampleOctaArray(IrradianceArray, normal, probeIndices[i]);
                prefilteredColor += probeWeights[i] * SampleOctaArrayLod(PrefilterArray, normal, probeIndices[i], roughness * MAX_REFLECTION_LOD);
        }

        if (irradianceColor == vec3(0.0f) && ProbeCount > 0)
        {
                irradianceColor = SampleOctaArray(IrradianceArray, normal, 0.0f);
                prefilteredColor = SampleOctaArrayLod(PrefilterArray, normal, 0.0f, roughness * MAX_REFLECTION_LOD);
        }

	float roughness = rmse.x;
  	float metallic = rmse.y;
	float specularIntensity = rmse.z;
	float emissiveIntensity = rmse.w;

	vec3 CameraPosition = InverseViewMatrix[3].xyz;
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
        vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
        vec3 ambient = (kD * diffuse + specular) * ao;

	OutLo = ambient + InLo + emissiveIntensity * albedoColor;
}
