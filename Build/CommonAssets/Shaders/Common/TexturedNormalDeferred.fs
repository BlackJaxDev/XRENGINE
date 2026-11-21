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

uniform sampler2D Texture0;
uniform sampler2D Texture1;

uniform vec3 BaseColor = vec3(1.0f, 1.0f, 1.0f);
uniform float Opacity = 1.0f;
uniform float Specular = 1.0f;
uniform float Roughness = 0.0f;
uniform float Metallic = 0.0f;
uniform float Emission = 0.0f;
uniform float AlphaCutoff = -1.0f;

// Snippets after uniforms so SurfaceDetailNormalMapping can reference Texture1.
#pragma snippet "NormalEncoding"
#pragma snippet "SurfaceDetailNormalMapping"
#pragma snippet "SurfaceDetailParallax"
#pragma snippet "DitheredTransparency"
#pragma snippet "SurfaceEmission"

vec3 getNormalFromMap(vec2 uv)
{
    return XRENGINE_GetSurfaceDetailNormal(uv, FragPos, FragTan, FragBinorm, FragNorm);
}

void main()
{
    vec2 uv = XRENGINE_BeginSurfaceParallax(FragUV0, FragPos, FragNorm);
    vec3 normal = getNormalFromMap(uv);
    vec4 texColor = XRENGINE_SampleSurface(Texture0, uv);

    // Alpha cutoff (masked mode) and dithered transparency.
    XRENGINE_AlphaCutoffAndDither(AlphaCutoff, texColor.a, Opacity, gl_FragCoord.xy);

    TransformId = FragRenderIdentityId;
    Normal = XRENGINE_EncodeNormal(normal);
    AlbedoOpacity = vec4(texColor.rgb * BaseColor, Opacity);
    RMSI = vec4(Roughness, Metallic, Specular, Emission);
    EmissionColor = XRENGINE_ResolveSurfaceEmission(uv, FragUV1, BaseColor, Emission);
    XRENGINE_EndSurfaceParallax();
}
