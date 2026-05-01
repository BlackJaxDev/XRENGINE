// Ambient occlusion sampling helpers for lit forward shaders.

layout(binding = 14) uniform sampler2D AmbientOcclusionTexture;
layout(binding = 25) uniform sampler2DArray AmbientOcclusionTextureArray;
uniform bool AmbientOcclusionEnabled;
uniform bool AmbientOcclusionArrayEnabled;
uniform float AmbientOcclusionPower;
#ifndef XRENGINE_SCREEN_SIZE_UNIFORMS
#define XRENGINE_SCREEN_SIZE_UNIFORMS
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif
#ifndef XRENGINE_SCREEN_ORIGIN_UNIFORM
#define XRENGINE_SCREEN_ORIGIN_UNIFORM
uniform vec2 ScreenOrigin;
#endif

// DEBUG uniform: when > 0 the sampled AO is raised to this power so the
// effect becomes dramatically visible (e.g. 4.0 turns AO 0.9 → 0.66).
// Set to 0 (default) for normal behaviour.
uniform float DebugForwardAOPower;

float XRENGINE_SampleAmbientOcclusion()
{
    if (!AmbientOcclusionEnabled)
        return 1.0;

    ivec2 aoSize = AmbientOcclusionArrayEnabled
        ? textureSize(AmbientOcclusionTextureArray, 0).xy
        : textureSize(AmbientOcclusionTexture, 0);
    if (aoSize.x <= 0 || aoSize.y <= 0)
        return 1.0;

    vec2 viewportSize = max(vec2(ScreenWidth, ScreenHeight), vec2(1.0));
    vec2 fragCoordLocal = gl_FragCoord.xy - ScreenOrigin;
    vec2 aoUv = clamp(fragCoordLocal / viewportSize, vec2(0.0), vec2(0.999999));
    ivec2 pixel = ivec2(floor(aoUv * vec2(aoSize)));
    float ao = AmbientOcclusionArrayEnabled
        ? texelFetch(AmbientOcclusionTextureArray, ivec3(pixel, XRENGINE_GetForwardViewIndex()), 0).r
        : texelFetch(AmbientOcclusionTexture, pixel, 0).r;
    ao = pow(clamp(ao, 0.0, 1.0), max(AmbientOcclusionPower, 0.001));

    // When the debug power is active, exaggerate the AO so even subtle
    // occlusion becomes plainly visible.
    if (DebugForwardAOPower > 0.0)
        ao = pow(clamp(ao, 0.0, 1.0), DebugForwardAOPower);

    return ao;
}
