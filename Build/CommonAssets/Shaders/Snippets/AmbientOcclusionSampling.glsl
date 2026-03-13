// Ambient occlusion sampling helpers for lit forward shaders.

layout(binding = 14) uniform sampler2D AmbientOcclusionTexture;
uniform bool AmbientOcclusionEnabled;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

float XRENGINE_SampleAmbientOcclusion()
{
    if (!AmbientOcclusionEnabled)
        return 1.0;

    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
        return 1.0;

    vec2 uv = (gl_FragCoord.xy - ScreenOrigin) / vec2(ScreenWidth, ScreenHeight);
    return texture(AmbientOcclusionTexture, clamp(uv, vec2(0.0), vec2(1.0))).r;
}