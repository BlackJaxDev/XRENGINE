// Spatially hashed AO gather shader based on
// https://interplayoflight.wordpress.com/2025/11/23/spatial-hashing-for-raytraced-ambient-occlusion
#version 450
#extension GL_OVR_multiview2 : require

const float PI = 3.14159265359f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Normal; //Normal
uniform sampler2D SSAONoiseTexture; //Noise
uniform sampler2D DepthView; //Depth

uniform vec3 Samples[128];
uniform int KernelSize = 96;
uniform float Radius = 0.9f;
uniform float Power = 1.2f;
uniform float Bias = 0.03f;
uniform int RayStepCount = 6;
uniform float CellSize = 0.75f;
uniform float MaxRayDistance = 1.5f;
uniform float Thickness = 0.1f;
uniform float DistanceFade = 1.0f;
uniform vec2 NoiseScale;

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

// Hash of the cell coordinate to decorrelate rays across neighboring pixels
uint Hash(uvec3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16;
    v += v << 3;
    v ^= v >> 4;
    v *= 268582165u;
    v ^= v >> 15;
    return v.x;
}

// Convert hash to [0,1) for randomized stepping
float HashToUnitFloat(uvec3 v)
{
    return float(Hash(v)) / float(0xffffffffu);
}

// Recover view-space position from a depth sample
vec3 ViewPosFromDepth(float depth, vec2 uv)
{
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;

    uv = uv * 0.5f + 0.5f;

    vec3 encodedNormal = texture(Normal, uv).rgb;
    float depth = texture(DepthView, uv).r;
    vec3 fragPosVS = ViewPosFromDepth(depth, uv);

    vec3 randomVec = vec3(texture(SSAONoiseTexture, uv * NoiseScale).rg * 2.0f - 1.0f, 0.0f);
    vec3 viewNormal = normalize((inverse(InverseViewMatrix) * vec4(encodedNormal, 0.0f)).rgb);
    vec3 viewTangent = normalize(randomVec - viewNormal * dot(randomVec, viewNormal));
    vec3 viewBitangent = cross(viewNormal, viewTangent);
    mat3 TBN = mat3(viewTangent, viewBitangent, viewNormal);

    float occlusion = 0.0f;
    float stepLength = MaxRayDistance / float(max(RayStepCount, 1));
    uvec3 cell = uvec3(floor((fragPosVS + viewNormal * Bias) / CellSize));

    for (int i = 0; i < KernelSize; ++i)
    {
        vec3 dir = normalize(TBN * Samples[i]);
        float randomizedStart = HashToUnitFloat(cell + uvec3(i)) * stepLength;
        float traveled = randomizedStart;

        for (int stepIdx = 0; stepIdx < RayStepCount; ++stepIdx)
        {
            float marchDist = min(traveled, MaxRayDistance);
            vec3 samplePos = fragPosVS + dir * (Bias + marchDist);
            vec4 clipPos = ProjMatrix * vec4(samplePos, 1.0f);
            clipPos.xyz /= clipPos.w;
            vec2 sampleUV = clipPos.xy * 0.5f + 0.5f;

            if (sampleUV.x < 0.0f || sampleUV.x > 1.0f || sampleUV.y < 0.0f || sampleUV.y > 1.0f)
                break;

            float sceneDepth = texture(DepthView, sampleUV).r;
            float sceneDepthVS = ViewPosFromDepth(sceneDepth, sampleUV).z;
            float expectedDepth = samplePos.z;

            if (sceneDepthVS < expectedDepth + Thickness)
            {
                float depthDiff = expectedDepth - sceneDepthVS;
                float falloff = clamp(1.0f - depthDiff / (Radius + Thickness), 0.0f, 1.0f);
                float distWeight = 1.0f / (1.0f + marchDist * DistanceFade);
                occlusion += falloff * distWeight;
                break;
            }

            traveled += stepLength;
            if (traveled > MaxRayDistance)
                break;
        }
    }

    float occlusionTerm = clamp(occlusion / float(KernelSize), 0.0f, 1.0f);
    OutIntensity = pow(1.0f - occlusionTerm, Power);
}
