#version 450
#extension GL_OVR_multiview2 : require

const float PI = 3.14159265359f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Texture0; // Normal
uniform sampler2D Texture1; // Noise
uniform sampler2D Texture2; // Depth

uniform vec3 Samples[128];
uniform int KernelSize;
uniform vec2 NoiseScale;

uniform float Radius = 0.9f;
uniform float SecondaryRadius = 1.6f;
uniform float Bias = 0.03f;
uniform float Power = 1.4f;
uniform float MultiViewBlend = 0.6f;
uniform float MultiViewSpread = 0.5f;

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

vec3 ViewPosFromDepth(float depth, vec2 uv)
{
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

float SampleAO(vec3 fragPosVS, vec3 samplePosVS)
{
    vec4 offset = ProjMatrix * vec4(samplePosVS, 1.0f);
    offset.xyz /= offset.w;
    vec2 sampleUV = offset.xy * 0.5f + 0.5f;
    if (sampleUV.x < 0.0f || sampleUV.x > 1.0f || sampleUV.y < 0.0f || sampleUV.y > 1.0f)
        return 0.0f;

    float sampleDepth = texture(Texture2, sampleUV).r;
    vec3 sampleView = ViewPosFromDepth(sampleDepth, sampleUV);

    float delta = sampleView.z - samplePosVS.z;
    float range = abs(fragPosVS.z - sampleView.z);
    float radius = max(SecondaryRadius, Radius);
    float rangeCheck = smoothstep(0.0f, 1.0f, 1.0f - clamp(range / radius, 0.0f, 1.0f));
    float occluded = delta >= Bias ? 1.0f : 0.0f;
    return occluded * rangeCheck;
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    vec3 normal = texture(Texture0, uv).rgb;
    float depth = texture(Texture2, uv).r;

    vec3 fragPosVS = ViewPosFromDepth(depth, uv);

    vec3 randomVec = vec3(texture(Texture1, uv * NoiseScale).rg * 2.0f - 1.0f, 0.0f);
    vec3 viewNormal = normalize((inverse(InverseViewMatrix) * vec4(normal, 0.0f)).rgb);
    vec3 viewTangent = normalize(randomVec - viewNormal * dot(randomVec, viewNormal));
    vec3 viewBitangent = cross(viewNormal, viewTangent);
    mat3 TBN = mat3(viewTangent, viewBitangent, viewNormal);

    float occlusionAccum = 0.0f;
    float weightAccum = 0.0f;

    for (int i = 0; i < 128; ++i)
    {
        if (i >= KernelSize)
            break;
        vec3 sampleDir = normalize(Samples[i]);
        vec3 viewSample = normalize(TBN * sampleDir);

        float alignment = max(dot(viewNormal, viewSample), 0.05f);
        float primary = SampleAO(fragPosVS, fragPosVS + viewSample * Radius);
        float secondary = SampleAO(fragPosVS, fragPosVS + viewSample * SecondaryRadius);

        vec3 tangentDir = viewSample - viewNormal * dot(viewSample, viewNormal);
        float tangentLength = length(tangentDir);
        float multiView = 0.0f;
        if (tangentLength > 0.0001f)
        {
            tangentDir /= tangentLength;
            float tangentRadius = mix(Radius, SecondaryRadius, clamp(MultiViewSpread, 0.0f, 1.0f));
            multiView = 0.5f * (
                SampleAO(fragPosVS, fragPosVS + tangentDir * tangentRadius) +
                SampleAO(fragPosVS, fragPosVS - tangentDir * tangentRadius));
        }

    float blended = mix(primary, multiView, clamp(MultiViewBlend, 0.0f, 1.0f));
    blended = mix(blended, secondary, 0.35f);

        occlusionAccum += blended * alignment;
        weightAccum += alignment;
    }

    float ao = weightAccum > 0.0f ? occlusionAccum / weightAccum : 0.0f;
    OutIntensity = pow(clamp(1.0f - ao, 0.0f, 1.0f), Power);
}
