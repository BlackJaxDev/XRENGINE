#version 450

layout (location = 0) out vec4 OutColor;

uniform float MatSpecularIntensity;
uniform float MatShininess;
uniform float AlphaCutoff;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo (diffuse)
uniform sampler2D Texture1; // Normal map (tangent space)
uniform sampler2D Texture2; // Alpha mask (R channel)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 2) in vec3 FragBinorm;
layout (location = 3) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"

vec3 getNormalFromMap()
{
    vec3 normal = texture(Texture1, FragUV0).rgb;
    normal = normalize(normal * 2.0 - 1.0);

    vec3 T = normalize(FragTan);
    vec3 N = normalize(FragNorm);
    vec3 B = cross(N, T);

    mat3 tbn = mat3(T, B, N);
    return normalize(tbn * normal);
}

void main()
{
    float alphaMask = texture(Texture2, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = 1.0;

    vec3 normal = getNormalFromMap();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);

    OutColor = vec4(texColor.rgb * totalLight, texColor.a * alphaMask);
}