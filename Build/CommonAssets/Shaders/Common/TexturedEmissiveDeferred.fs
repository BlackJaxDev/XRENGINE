#version 450

#pragma snippet "NormalEncoding"

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec2 Normal;
layout (location = 2) out vec4 RMSE;
layout (location = 3) out uint TransformId;

layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;
layout (location = 21) in float FragTransformId;

uniform sampler2D Texture0; //Albedo
uniform sampler2D Texture1; //Emissive

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;

void main()
{
    vec4 albedoSample = texture(Texture0, FragUV0);

    TransformId = floatBitsToUint(FragTransformId);
    Normal = XRENGINE_EncodeNormal(FragNorm);
    AlbedoOpacity = vec4(albedoSample.rgb * BaseColor, Opacity);
    float emissive = texture(Texture1, FragUV0).r * Emission;
    RMSE = vec4(Roughness, Metallic, Specular, emissive);
}
