// Ambient occlusion sampling helpers for lit forward shaders.

layout(binding = 14) uniform sampler2D AmbientOcclusionTexture;
uniform bool AmbientOcclusionEnabled;
uniform float AmbientOcclusionPower;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

// DEBUG uniform: when > 0 the sampled AO is raised to this power so the
// effect becomes dramatically visible (e.g. 4.0 turns AO 0.9 → 0.66).
// Set to 0 (default) for normal behaviour.
uniform float DebugForwardAOPower;

float XRENGINE_SampleAmbientOcclusion()
{
    if (!AmbientOcclusionEnabled)
        return 1.0;

    ivec2 aoSize = textureSize(AmbientOcclusionTexture, 0);
    if (aoSize.x <= 0 || aoSize.y <= 0)
        return 1.0;

    ivec2 pixel = ivec2(floor(gl_FragCoord.xy - ScreenOrigin));
    pixel = clamp(pixel, ivec2(0), aoSize - ivec2(1));
    float ao = texelFetch(AmbientOcclusionTexture, pixel, 0).r;
    ao = pow(clamp(ao, 0.0, 1.0), max(AmbientOcclusionPower, 0.001));

    // When the debug power is active, exaggerate the AO so even subtle
    // occlusion becomes plainly visible.
    if (DebugForwardAOPower > 0.0)
        ao = pow(clamp(ao, 0.0, 1.0), DebugForwardAOPower);

    return ao;
}