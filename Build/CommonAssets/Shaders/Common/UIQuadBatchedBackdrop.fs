#version 460

layout (location = 4) in vec2 FragUV0;
layout (location = 5) flat in vec4 InstanceColor;
layout (location = 6) flat in vec4 InstanceBounds;
layout (location = 7) in vec2 FragLocalPos;
layout (location = 8) flat in vec4 InstanceCornerRadii;
layout (location = 9) flat in vec4 InstanceStyleMeta;
layout (location = 10) flat in vec4 InstanceBorderColor;
layout (location = 11) flat in vec4 InstanceGradientBottom;
layout (location = 12) flat in vec4 InstanceShadowColor;
layout (location = 13) flat in vec4 InstanceShadowParams;
layout (location = 14) flat in vec4 InstanceBlurParams;

// Mip pyramid of the scene color behind the UI, written by VPRC_PrepareUiBackdropBlur.
uniform sampler2D Texture0;
uniform vec2 UiViewportSize;

layout (location = 0) out vec4 OutColor;

#pragma snippet "UIQuadStyle"

// Frosted styled quad. Each quad picks its own blur level from the shared pyramid
// with a trilinear LOD, so one texture binding serves every blur radius.
void main()
{
    bool useBackdrop = (XRENGINE_UiStyleFlags() & XRENGINE_UiFlagBackdropBlur) != 0u &&
        InstanceBlurParams.y > 0.001;
    vec3 backdrop = vec3(0.0);
    if (useBackdrop)
    {
        vec2 screenUV = clamp(gl_FragCoord.xy / max(UiViewportSize, vec2(1.0)), vec2(0.0), vec2(1.0));
        float radiusNorm = clamp(InstanceBlurParams.x / 28.0, 0.0, 1.0);
        backdrop = textureLod(Texture0, screenUV, radiusNorm * 1.65).rgb;
    }
    bool discardFragment;
    vec4 color = XRENGINE_UiShadeStyledQuad(useBackdrop, backdrop, discardFragment);
    if (discardFragment)
        discard;
    OutColor = color;
}
