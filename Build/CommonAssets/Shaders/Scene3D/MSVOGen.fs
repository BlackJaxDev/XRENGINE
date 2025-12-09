#version 450

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Normal; //Normal
uniform sampler2D DepthView; //Depth

uniform vec4 ScaleFactors; // Multi-scale factors
uniform float Bias = 0.05f;
uniform float Intensity = 1.0f;
uniform float ScreenWidth;
uniform float ScreenHeight;

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

vec3 ViewPosFromDepth(float depth, vec2 uv)
{
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

float ComputeObscurance(vec3 pos, vec3 normal, float radius, vec2 texCoord)
 {
    float occlusion = 0.0f;

    int numSamples = 8;
    float stepAngle = 2.0f * PI / float(numSamples);

    for (int i = 0; i < numSamples; ++i)
    {
        float angle = stepAngle * float(i);
        vec2 sampleOffset = vec2(cos(angle), sin(angle)) * radius / vec2(ScreenWidth, ScreenHeight);

        vec2 uv = texCoord + sampleOffset;
        float depth = texture(DepthView, uv).r;
        vec3 samplePos = ViewPosFromDepth(depth, uv);
        vec3 diff = samplePos - pos;

        float dist = length(diff);
        float dotProduct = max(dot(normal, normalize(diff)), 0.0f);
        occlusion += max(radius - dist, 0.0f) * dotProduct;
    }

    return occlusion / float(numSamples);
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    //Normalize uv from [-1, 1] to [0, 1]
    uv = uv * 0.5f + 0.5f;
    
    vec3 normal = texture(Normal, uv).rgb;
    vec3 viewNormal = normalize((inverse(InverseViewMatrix) * vec4(normal, 0.0f)).rgb);
    float depth = texture(DepthView, uv).r;
    vec3 position = ViewPosFromDepth(depth, uv);

    float totalOcclusion = 0.0f;
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.x, uv);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.y, uv);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.z, uv);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.w, uv);
    OutIntensity = 0.0f;//clamp(1.0f - Bias * totalOcclusion * Intensity, 0.0f, 1.0f);
}
