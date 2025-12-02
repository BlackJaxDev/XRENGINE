#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragPos;

uniform sampler2D Texture0;
uniform float Roughness = 0.0f;
uniform int SourceDim = 512;

const float PI = 3.14159265359f;

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
        vec2 signDir = vec2(uv.x >= 0.0f ? 1.0f : -1.0f, uv.y >= 0.0f ? 1.0f : -1.0f);
        uv = (1.0f - abs(uv.yx)) * signDir;
    }

    return uv * 0.5f + 0.5f;
}

vec3 DecodeOcta(vec2 uv)
{
    vec2 f = uv * 2.0f - 1.0f;
    vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));

    if (n.z < 0.0f)
    {
        vec2 nXY = n.xy;
        vec2 signDir = vec2(nXY.x >= 0.0f ? 1.0f : -1.0f, nXY.y >= 0.0f ? 1.0f : -1.0f);
        n.xy = (1.0f - abs(nXY.yx)) * signDir;
    }

    // Swizzle back: octahedral (x,y,z) -> world (x,z,y)
    vec3 dir = vec3(n.x, n.z, n.y);
    return normalize(dir);
}

vec3 DirectionFromFragPos(vec3 fragPos)
{
    vec2 clipXY = fragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;
    return DecodeOcta(uv);
}

vec3 SampleOcta(sampler2D tex, vec3 dir, float lod)
{
    vec2 uv = EncodeOcta(dir);
    return textureLod(tex, uv, lod).rgb;
}

float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0f);
    float NdotH2 = NdotH * NdotH;

    float nom   = a2;
    float denom = (NdotH2 * (a2 - 1.0f) + 1.0f);
    denom = PI * denom * denom;

    return nom / denom;
}

float RadicalInverse_VdC(uint bits)
{
     bits = (bits << 16u) | (bits >> 16u);
     bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
     bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
     bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
     bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
     return float(bits) * 2.3283064365386963e-10f;
}

vec2 Hammersley(uint i, uint N)
{
    return vec2(float(i) / float(N), RadicalInverse_VdC(i));
}

vec3 ImportanceSampleGGX(vec2 Xi, vec3 N, float roughness)
{
    float a = roughness * roughness;

    float phi = 2.0f * PI * Xi.x;
    float cosTheta = sqrt((1.0f - Xi.y) / (1.0f + (a * a - 1.0f) * Xi.y));
    float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

    vec3 H;
    H.x = cos(phi) * sinTheta;
    H.y = sin(phi) * sinTheta;
    H.z = cosTheta;

    vec3 up        = abs(N.z) < 0.999f ? vec3(0.0f, 0.0f, 1.0f) : vec3(1.0f, 0.0f, 0.0f);
    vec3 tangent   = normalize(cross(up, N));
    vec3 bitangent = cross(N, tangent);

    vec3 sampleVec = tangent * H.x + bitangent * H.y + N * H.z;
    return normalize(sampleVec);
}

void main()
{
    vec3 N = DirectionFromFragPos(FragPos);
    vec3 R = N;
    vec3 V = R;

    const uint SAMPLE_COUNT = 1024u;
    vec3 prefilteredColor = vec3(0.0f);
    float totalWeight = 0.0f;

    for (uint i = 0u; i < SAMPLE_COUNT; ++i)
    {
        vec2 Xi = Hammersley(i, SAMPLE_COUNT);
        vec3 H  = ImportanceSampleGGX(Xi, N, Roughness);
        vec3 L  = normalize(2.0f * dot(V, H) * H - V);

        float NdotL = dot(N, L);
        if (NdotL > 0.0f)
        {
            float D     = DistributionGGX(N, H, Roughness);
            float NdotH = max(dot(N, H), 0.0f);
            float HdotV = max(dot(H, V), 0.0f);
            float pdf   = D * NdotH / (4.0f * HdotV) + 0.0001f;

            float res      = float(SourceDim);
            float saTexel  = 4.0f * PI / (6.0f * res * res);
            float saSample = 1.0f / (float(SAMPLE_COUNT) * pdf + 0.0001f);

            float mipLevel = Roughness == 0.0f ? 0.0f : 0.5f * log2(saSample / saTexel);

            prefilteredColor += SampleOcta(Texture0, L, mipLevel) * NdotL;
            totalWeight      += NdotL;
        }
    }

    OutColor = prefilteredColor / max(totalWeight, 1e-4f);
}
