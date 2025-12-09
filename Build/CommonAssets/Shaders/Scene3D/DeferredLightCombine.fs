#version 450

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;
// Reflection mip is derived from the texture's mip count; no fixed cap.
const float MAX_REFLECTION_LOD = 4.0f; // Deprecated; retained to avoid breaking includes, actual max is queried per texture.

layout(location = 0) out vec3 OutLo; //Diffuse Light Color, to start off the HDR Scene Texture
layout(location = 0) in vec3 FragPos;

layout(binding = 0) uniform sampler2D AlbedoOpacity;
layout(binding = 1) uniform sampler2D Normal;
layout(binding = 2) uniform sampler2D RMSE;
layout(binding = 3) uniform sampler2D SSAOIntensityTexture;
layout(binding = 4) uniform sampler2D DepthView; //Depth
layout(binding = 5) uniform sampler2D LightingTexture;

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

struct ProbeParam
{
        vec4 InfluenceInner;       // xyz inner extents or inner radius (x) for sphere
        vec4 InfluenceOuter;       // xyz outer extents or outer radius (x) for sphere
        vec4 InfluenceOffsetShape; // xyz offset, w = shape (0 sphere, 1 box)
        vec4 ProxyCenterEnable;    // xyz center offset, w = parallax enabled (1/0)
        vec4 ProxyHalfExtents;     // xyz half extents, w = normalization scale
        vec4 ProxyRotation;        // xyzw quaternion
};

layout(std430, binding = 2) buffer LightProbeParameters
{
        ProbeParam ProbeParams[];
};

// Sparse grid accelerator: flattened cells with offsets into a compact probe list
layout(std430, binding = 3) buffer LightProbeGridCells
{
        ivec2 CellOffsetCount[]; // x=offset, y=count
};

layout(std430, binding = 4) buffer LightProbeGridIndices
{
        int CellProbeIndices[];
};

uniform int ProbeCount;
uniform int TetraCount;
uniform ivec3 ProbeGridDims;
uniform vec3 ProbeGridOrigin;
uniform float ProbeGridCellSize;
uniform bool UseProbeGrid;
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

mat3 QuaternionToMat3(vec4 q)
{
        vec3 q2 = q.xyz + q.xyz;
        vec3 qq = q.xyz * q2;
        float qx2 = q.x * q2.x;
        float qy2 = q.y * q2.y;
        float qz2 = q.z * q2.z;

        vec3 qwq = q.w * q2;
        vec3 m0 = vec3(1.0f - (qy2 + qz2), qq.x + qwq.z, qq.y - qwq.y);
        vec3 m1 = vec3(qq.x - qwq.z, 1.0f - (qx2 + qz2), qq.z + qwq.x);
        vec3 m2 = vec3(qq.y + qwq.y, qq.z - qwq.x, 1.0f - (qx2 + qy2));
        return mat3(m0, m1, m2);
}

float ComputeInfluenceWeight(int probeIndex, vec3 worldPos)
{
        ProbeParam p = ProbeParams[probeIndex];
        int shape = int(p.InfluenceOffsetShape.w + 0.5f);
        vec3 center = ProbePositions[probeIndex].xyz + p.InfluenceOffsetShape.xyz;
        if (shape == 0)
        {
                float inner = p.InfluenceInner.x;
                float outer = max(p.InfluenceOuter.x, inner + 0.0001f);
                float dist = length(worldPos - center);
                float ndf = clamp((dist - inner) / (outer - inner), 0.0f, 1.0f);
                return 1.0f - ndf;
        }
        vec3 inner = p.InfluenceInner.xyz;
        vec3 outer = max(p.InfluenceOuter.xyz, inner + vec3(0.0001f));
        vec3 rel = abs(worldPos - center);
        vec3 ndf3 = clamp((rel - inner) / (outer - inner), 0.0f, 1.0f);
        float ndf = max(ndf3.x, max(ndf3.y, ndf3.z));
        return 1.0f - ndf;
}

vec3 ApplyParallax(int probeIndex, vec3 dirWS, vec3 worldPos)
{
        ProbeParam p = ProbeParams[probeIndex];
        if (p.ProxyCenterEnable.w < 0.5f)
                return dirWS;

        vec3 proxyCenter = ProbePositions[probeIndex].xyz + p.ProxyCenterEnable.xyz;
        vec3 halfExt = max(p.ProxyHalfExtents.xyz, vec3(0.0001f));
        mat3 rot = QuaternionToMat3(p.ProxyRotation);
        mat3 invRot = transpose(rot);

        vec3 rayOrigLS = invRot * (worldPos - proxyCenter);
        vec3 rayDirLS = invRot * dirWS;
        rayDirLS = normalize(rayDirLS);

        vec3 t1 = (halfExt - rayOrigLS) / max(rayDirLS, vec3(1e-6f));
        vec3 t2 = (-halfExt - rayOrigLS) / max(rayDirLS, vec3(1e-6f));
        vec3 tmax = max(t1, t2);
        float dist = min(tmax.x, min(tmax.y, tmax.z));
        if (dist <= 0.0f || isnan(dist) || isinf(dist))
                return dirWS;

        vec3 hitLS = rayOrigLS + rayDirLS * dist;
        vec3 hitWS = proxyCenter + rot * hitLS;
        return normalize(hitWS - ProbePositions[probeIndex].xyz);
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

void ResolveProbeWeightsGrid(vec3 worldPos, out vec4 weights, out ivec4 indices)
{
        weights = vec4(0.0f);
        indices = ivec4(-1);

        if (ProbeGridDims.x <= 0 || ProbeGridDims.y <= 0 || ProbeGridDims.z <= 0 || ProbeGridCellSize <= 0.0f)
                return;

        vec3 rel = (worldPos - ProbeGridOrigin) / ProbeGridCellSize;
        ivec3 cell = clamp(ivec3(floor(rel)), ivec3(0), ProbeGridDims - ivec3(1));
        int flatIndex = cell.x + cell.y * ProbeGridDims.x + cell.z * ProbeGridDims.x * ProbeGridDims.y;

        ivec2 oc = CellOffsetCount[flatIndex];
        int offset = oc.x;
        int count = oc.y;
        if (count <= 0)
                return;

        float bestDistances[4];
        int bestIndices[4];
        for (int k = 0; k < 4; ++k)
        {
                bestDistances[k] = 1e20f;
                bestIndices[k] = -1;
        }

        for (int i = 0; i < count; ++i)
        {
                int probeIndex = CellProbeIndices[offset + i];
                if (probeIndex < 0 || probeIndex >= ProbeCount)
                        continue;
                float d = length(worldPos - ProbePositions[probeIndex].xyz);
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
                                bestIndices[k] = probeIndex;
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

        vec3 albedoColor = texture(AlbedoOpacity, uv).rgb;
        vec3 normal = texture(Normal, uv).rgb;
        vec4 rmse = texture(RMSE, uv);
        float ao = texture(SSAOIntensityTexture, uv).r;
        float depth = texture(DepthView, uv).r;
        vec3 InLo = max(texture(LightingTexture, uv).rgb, vec3(0.0f));
        vec3 fragPosWS = WorldPosFromDepth(depth, uv);
        //float fogDensity = noise3(fragPosWS);

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

        vec4 probeWeights; 
        ivec4 probeIndices; 
        if (UseProbeGrid)
        {
                ResolveProbeWeightsGrid(fragPosWS, probeWeights, probeIndices);
                if (probeIndices.x < 0 && probeIndices.y < 0 && probeIndices.z < 0 && probeIndices.w < 0)
                        ResolveProbeWeights(fragPosWS, probeWeights, probeIndices);
        }
        else
        {
                ResolveProbeWeights(fragPosWS, probeWeights, probeIndices);
        }

        vec3 irradianceColor = vec3(0.0f); 
        vec3 prefilteredColor = vec3(0.0f); 
        float totalWeight = 0.0f;

        for (int i = 0; i < 4; ++i) 
        { 
                if (probeIndices[i] < 0) 
                        continue; 
                float influence = ComputeInfluenceWeight(probeIndices[i], fragPosWS);
                float w = probeWeights[i] * influence;
                if (w <= 0.0f)
                        continue;
                vec3 parallaxDir = ApplyParallax(probeIndices[i], normal, fragPosWS);
                vec3 reflDir = ApplyParallax(probeIndices[i], R, fragPosWS);
                
                // Fetch normalization scale (allows normalized probes to share textures)
                float normScale = ProbeParams[probeIndices[i]].ProxyHalfExtents.w;
                normScale = max(normScale, 0.0001f);
                
                // Clamp mip level to prefilter array's available mips
                float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
                float desiredLod = roughness * MAX_REFLECTION_LOD;
                float clampedLod = min(desiredLod, maxMip);
                
                irradianceColor += w * normScale * SampleOctaArray(IrradianceArray, parallaxDir, probeIndices[i]);
                prefilteredColor += w * normScale * SampleOctaArrayLod(PrefilterArray, reflDir, probeIndices[i], clampedLod);
                totalWeight += w;
        } 

        if (totalWeight > 0.0f)
        {
                irradianceColor /= totalWeight;
                prefilteredColor /= totalWeight;
        }
        else if (ProbeCount > 0) 
        { 
                float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
                float clampedLod = min(roughness * MAX_REFLECTION_LOD, maxMip);
                irradianceColor = SampleOctaArray(IrradianceArray, normal, 0.0f); 
                prefilteredColor = SampleOctaArrayLod(PrefilterArray, normal, 0.0f, clampedLod); 
        } 

        vec3 diffuse = irradianceColor * albedoColor;
        vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
        vec3 ambient = (kD * diffuse + specular) * ao;

        OutLo = ambient + InLo + emissiveIntensity * albedoColor;
}
