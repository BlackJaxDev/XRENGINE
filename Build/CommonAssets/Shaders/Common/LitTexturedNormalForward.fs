#version 450

layout (location = 0) out vec4 OutColor;

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Normal map (tangent space)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 2) in vec3 FragBinorm;
layout (location = 3) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"

vec3 getNormalFromMap()
{
    vec3 normal = texture(Texture1, FragUV0).rgb;
    normal = normalize(normal * 2.0 - 1.0);
    
    // Convert from DirectX to OpenGL normal map convention (flip green/Y channel)
    //normal.y = -normal.y;
    
    vec3 T = normalize(FragTan);
    vec3 N = normalize(FragNorm);
    // Reconstruct bitangent from cross product for consistent handedness
    vec3 B = cross(N, T);
    
    mat3 tbn = mat3(T, B, N);
    return normalize(tbn * normal);
}

void main()
{
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();

    vec3 normal = getNormalFromMap();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);

    OutColor = texColor * vec4(totalLight, 1.0);
}
