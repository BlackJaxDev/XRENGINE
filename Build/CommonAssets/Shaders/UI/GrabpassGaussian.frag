#version 450
#pragma snippet "ScreenSpaceUtils"

layout (location = 0) out vec4 OutColor;

uniform sampler2D Texture0;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int SampleCount;
uniform vec4 MatColor;
uniform float BlurStrength;

const int MaxBlurTaps = 12;
const float BlurCenterWeight = 0.16363636;
const vec2 BlurTapOffsets[MaxBlurTaps] = vec2[](
    vec2(-1.0,  0.0), vec2( 1.0,  0.0), vec2( 0.0, -1.0), vec2( 0.0,  1.0),
    vec2(-1.0, -1.0), vec2( 1.0, -1.0), vec2(-1.0,  1.0), vec2( 1.0,  1.0),
    vec2(-2.0,  0.0), vec2( 2.0,  0.0), vec2( 0.0, -2.0), vec2( 0.0,  2.0)
);
const float BlurTapWeights[MaxBlurTaps] = float[](
    0.10909091, 0.10909091, 0.10909091, 0.10909091,
    0.07272727, 0.07272727, 0.07272727, 0.07272727,
    0.02727273, 0.02727273, 0.02727273, 0.02727273
);

void main()
{
    // When the grab-pass mechanism is not available (e.g. Vulkan, where no
    // BlitViewportToFBO path exists yet), the grab-pass texture is never
    // populated and stays at its initial 1x1 size.  Detect this and output
    // fully transparent so the destination (skybox, scene) is preserved.
    ivec2 grabSize = textureSize(Texture0, 0);
    if (grabSize.x <= 1 && grabSize.y <= 1)
    {
        OutColor = vec4(0.0);
        return;
    }

    // gl_FragCoord lives in screen-space (ScreenWidth x ScreenHeight); derive the
    // UV from that so the mapping is correct even if the grab texture was captured
    // at a different resolution. grabSize is only used for the texel step below.
    vec2 texelSize = 1.0 / vec2(grabSize);
    vec2 uv = XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight));
    vec3 centerColor = texture(Texture0, uv).rgb;

    if (BlurStrength <= 0.001 || SampleCount <= 0)
    {
        // Source alpha is intentionally discarded; the grab is treated as opaque.
        OutColor = vec4(centerColor, 1.0) * MatColor;
        return;
    }

    vec2 blurStep = texelSize * BlurStrength;
    vec2 uvMin = texelSize * 0.5;
    vec2 uvMax = vec2(1.0) - uvMin;

    // The tap table is grouped in concentric rings (4 axis, 4 diagonal, 4 axis-2).
    // Snap to a ring boundary so a partial SampleCount can't produce a lopsided kernel.
    int requested = clamp(SampleCount, 0, MaxBlurTaps);
    int activeTapCount = requested >= 12 ? 12 : (requested >= 8 ? 8 : (requested >= 4 ? 4 : 0));

    // Truncated 5x5 Gaussian approximation with preweighted taps.
    vec3 col = centerColor * BlurCenterWeight;
    float totalWeight = BlurCenterWeight;

    for (int tapIndex = 0; tapIndex < activeTapCount; ++tapIndex)
    {
        vec2 sampleCoord = clamp(uv + BlurTapOffsets[tapIndex] * blurStep, uvMin, uvMax);
        float weight = BlurTapWeights[tapIndex];
        col += texture(Texture0, sampleCoord).rgb * weight;
        totalWeight += weight;
    }

    col /= totalWeight;
    OutColor = vec4(col, 1.0) * MatColor;
}
