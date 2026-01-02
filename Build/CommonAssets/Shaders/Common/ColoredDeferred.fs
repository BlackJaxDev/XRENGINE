#version 450

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec3 Normal;
layout (location = 2) out vec4 RMSE;
layout (location = 3) out uint TransformId;

layout (location = 1) in vec3 FragNorm;
layout (location = 21) in float FragTransformId;

uniform vec3 BaseColor;
uniform float Opacity = 1.0f;
uniform float Specular = 0.2f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;

void main()
{
    TransformId = floatBitsToUint(FragTransformId);
    Normal = normalize(FragNorm);
    AlbedoOpacity = vec4(BaseColor, Opacity);
    RMSE = vec4(Roughness, Metallic, Specular, Emission);
}
