#version 450

layout (location = 0) out vec4 AlbedoOpacity;
layout (location = 1) out vec2 Normal;
layout (location = 2) out vec4 RMSI;
layout (location = 3) out uint TransformId;
layout (location = 4) out vec4 EmissionColor;

layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;
layout (location = 5) in vec2 FragUV1;
layout (location = 21) flat in uint FragTransformId;
layout (location = 27) flat in uint FragRenderIdentityId;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Optional opacity mask (R channel)

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 0.2f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;
uniform float AlphaCutoff = -1.0f;

#pragma snippet "NormalEncoding"
#pragma snippet "DitheredTransparency"
#pragma snippet "SurfaceEmission"

void main()
{
    vec4 albedoSample = texture(Texture0, FragUV0);
    // glTF base-color alpha remains coverage even when the legacy positional
    // opacity slot aliases the same texture. A distinct opacity map multiplies it.
    float alphaMask = albedoSample.a * texture(Texture1, FragUV0).r;

    XRENGINE_AlphaCutoffAndDither(AlphaCutoff, alphaMask, Opacity, gl_FragCoord.xy);

    TransformId = FragRenderIdentityId;
    Normal = XRENGINE_EncodeNormal(normalize(FragNorm));
    AlbedoOpacity = vec4(albedoSample.rgb * BaseColor, Opacity * alphaMask);
    RMSI = vec4(Roughness, Metallic, Specular, Emission);
    EmissionColor = XRENGINE_ResolveSurfaceEmission(FragUV0, FragUV1, BaseColor, Emission);
}
