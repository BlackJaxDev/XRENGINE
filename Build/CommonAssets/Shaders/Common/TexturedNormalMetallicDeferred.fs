#version 450

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec2 Normal;
layout (location = 2) out vec4 RMSI;
layout (location = 3) out uint TransformId;
layout (location = 4) out vec4 EmissionColor;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 3) in vec3 FragBinorm;
layout (location = 2) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;
layout (location = 5) in vec2 FragUV1;
layout (location = 21) flat in uint FragTransformId;
layout (location = 27) flat in uint FragRenderIdentityId;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Normal
uniform sampler2D Texture2; // Metallic

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;

// Snippets after uniforms so SurfaceDetailNormalMapping can reference Texture1.
#pragma snippet "NormalEncoding"
#pragma snippet "SurfaceDetailNormalMapping"
#pragma snippet "SurfaceEmission"

vec3 getNormalFromMap()
{
    return XRENGINE_GetSurfaceDetailNormal(FragUV0, FragPos, FragTan, FragBinorm, FragNorm);
}

void main()
{
    vec3 normal = getNormalFromMap();
    vec4 albedoSample = texture(Texture0, FragUV0);

    TransformId = FragRenderIdentityId;
    Normal = XRENGINE_EncodeNormal(normal);
    AlbedoOpacity = vec4(albedoSample.rgb * BaseColor, Opacity);

    float metallicTex = texture(Texture2, FragUV0).r;
    RMSI = vec4(Roughness, Metallic * metallicTex, Specular, Emission);
    EmissionColor = XRENGINE_ResolveSurfaceEmission(FragUV0, FragUV1, BaseColor, Emission);
}
