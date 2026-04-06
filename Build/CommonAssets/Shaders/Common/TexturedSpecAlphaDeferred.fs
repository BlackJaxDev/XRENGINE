#version 450

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec2 Normal;
layout (location = 2) out vec4 RMSI;
layout (location = 3) out uint TransformId;

layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;
layout (location = 21) in float FragTransformId;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Specular map (intensity in R channel)
uniform sampler2D Texture2; // Alpha mask (R channel)

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;
uniform float AlphaCutoff = -1.0f;

#pragma snippet "NormalEncoding"
#pragma snippet "DitheredTransparency"

void main()
{
    vec4 albedoSample = texture(Texture0, FragUV0);
    float alphaMask = texture(Texture2, FragUV0).r;

    XRENGINE_AlphaCutoffAndDither(AlphaCutoff, alphaMask, Opacity, gl_FragCoord.xy);

    TransformId = floatBitsToUint(FragTransformId);
    Normal = XRENGINE_EncodeNormal(normalize(FragNorm));
    AlbedoOpacity = vec4(albedoSample.rgb * BaseColor, Opacity);

    float specularTex = texture(Texture1, FragUV0).r;
    RMSI = vec4(Roughness, Metallic, Specular * specularTex, Emission);
}