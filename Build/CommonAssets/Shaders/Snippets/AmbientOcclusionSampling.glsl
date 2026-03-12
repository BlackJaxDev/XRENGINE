// Ambient occlusion sampling helpers for lit forward shaders.

layout(binding = 14) uniform sampler2D AmbientOcclusionTexture;
uniform bool AmbientOcclusionEnabled;

float XRENGINE_SampleAmbientOcclusion()
{
    if (!AmbientOcclusionEnabled)
        return 1.0;

    vec2 aoTextureSize = vec2(textureSize(AmbientOcclusionTexture, 0));
    if (aoTextureSize.x <= 0.0 || aoTextureSize.y <= 0.0)
        return 1.0;

    vec2 uv = gl_FragCoord.xy / aoTextureSize;
    return texture(AmbientOcclusionTexture, clamp(uv, vec2(0.0), vec2(1.0))).r;
}