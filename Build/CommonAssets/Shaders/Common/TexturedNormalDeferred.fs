#version 450

#pragma snippet "NormalEncoding"
#pragma snippet "SurfaceDetailNormalMapping"

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec2 Normal;
layout (location = 2) out vec4 RMSI;
layout (location = 3) out uint TransformId;

layout (location = 1) in vec3 FragNorm;
layout (location = 3) in vec3 FragBinorm;
layout (location = 2) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;
layout (location = 21) in float FragTransformId;

uniform sampler2D Texture0;
uniform sampler2D Texture1;

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;

vec3 getNormalFromMap()
{
    return XRENGINE_GetSurfaceDetailNormal(FragUV0, FragTan, FragBinorm, FragNorm);
}

void main()
{
    TransformId = floatBitsToUint(FragTransformId);
    Normal = XRENGINE_EncodeNormal(getNormalFromMap());
    AlbedoOpacity = vec4(texture(Texture0, FragUV0).rgb * BaseColor, Opacity);
    RMSI = vec4(Roughness, Metallic, Specular, Emission);
}
