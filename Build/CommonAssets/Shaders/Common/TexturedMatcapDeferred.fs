#version 450

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec3 Normal;
layout (location = 2) out vec4 RMSI;
layout (location = 3) out uint TransformId;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;
layout (location = 21) in float FragTransformId;

uniform sampler2D Texture0;

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;

void main()
{
    TransformId = floatBitsToUint(FragTransformId);
    vec3 viewDir = normalize(-FragPos);
    vec3 reflected = reflect(viewDir, normalize(FragNorm));
    float m = 2.0f * sqrt(reflected.x * reflected.x + reflected.y * reflected.y + (reflected.z + 1.0f) * (reflected.z + 1.0f));
    vec2 matcapUV = vec2(reflected.x / m + 0.5f, reflected.y / m + 0.5f);

    Normal = normalize(FragNorm);
    AlbedoOpacity = vec4(texture(Texture0, matcapUV).rgb * BaseColor, Opacity);
    RMSI = vec4(Roughness, Metallic, Specular, Emission);
}
