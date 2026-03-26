#version 450

layout (triangles, equal_spacing, ccw) in;

layout (location = 0) in vec3 TcFragPos[];
layout (location = 1) in vec3 TcFragNorm[];
layout (location = 4) in vec2 TcFragUV0[];

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;

uniform mat4 ViewProjectionMatrix;
uniform float RenderTime;
uniform float OceanWaveIntensity;
uniform float WaveScale;
uniform float WaveSpeed;
uniform float WaveHeight;

float Hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float WaveField(vec2 worldXZ, float time)
{
    vec2 p = worldXZ * WaveScale;
    float a = sin(p.x * 1.1 + time * WaveSpeed);
    float b = cos(p.y * 1.3 - time * WaveSpeed * 1.2);
    float c = sin(dot(p, vec2(0.71, 1.27)) + time * WaveSpeed * 0.73);
    float n = Hash12(floor(p * 0.5)) * 2.0 - 1.0;
    return (a * 0.45 + b * 0.35 + c * 0.20 + n * 0.10) * WaveHeight * OceanWaveIntensity;
}

vec3 Displace(vec3 worldPos, float time)
{
    worldPos.y += WaveField(worldPos.xz, time);
    return worldPos;
}

vec3 EstimateNormal(vec3 worldPos, float time)
{
    const float eps = 0.06;
    vec3 px1 = Displace(worldPos + vec3(eps, 0.0, 0.0), time);
    vec3 px0 = Displace(worldPos - vec3(eps, 0.0, 0.0), time);
    vec3 pz1 = Displace(worldPos + vec3(0.0, 0.0, eps), time);
    vec3 pz0 = Displace(worldPos - vec3(0.0, 0.0, eps), time);
    return normalize(cross(pz1 - pz0, px1 - px0));
}

void main()
{
    vec3 bary = gl_TessCoord;
    vec3 worldPos =
        TcFragPos[0] * bary.x +
        TcFragPos[1] * bary.y +
        TcFragPos[2] * bary.z;

    vec2 uv =
        TcFragUV0[0] * bary.x +
        TcFragUV0[1] * bary.y +
        TcFragUV0[2] * bary.z;

    vec3 displaced = Displace(worldPos, RenderTime);

    FragPos = displaced;
    FragNorm = EstimateNormal(worldPos, RenderTime);
    FragUV0 = uv;
    gl_Position = ViewProjectionMatrix * vec4(displaced, 1.0);
}
