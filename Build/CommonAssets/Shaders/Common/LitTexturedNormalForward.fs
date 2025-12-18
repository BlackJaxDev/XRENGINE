#version 450

layout (location = 0) out vec4 OutColor;

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Normal map (tangent space)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 6) in vec2 FragUV0;

#pragma snippet "ForwardLighting"

// Derivative-based TBN reconstruction (no explicit tangent attribute required).
vec3 XRENGINE_GetNormalFromMap()
{
    vec3 tangentNormal = texture(Texture1, FragUV0).xyz * 2.0 - 1.0;

    vec3 Q1 = dFdx(FragPos);
    vec3 Q2 = dFdy(FragPos);
    vec2 st1 = dFdx(FragUV0);
    vec2 st2 = dFdy(FragUV0);

    vec3 N = normalize(FragNorm);
    vec3 T = normalize(Q1 * st2.t - Q2 * st1.t);
    vec3 B = -normalize(cross(N, T));
    mat3 TBN = mat3(T, B, N);

    return normalize(TBN * tangentNormal);
}

void main()
{
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = 1.0;

    vec3 normal = XRENGINE_GetNormalFromMap();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);

    OutColor = texColor * vec4(totalLight, 1.0);
}
