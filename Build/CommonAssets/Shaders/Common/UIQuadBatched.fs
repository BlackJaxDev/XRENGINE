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

layout (location = 0) out vec4 OutColor;

#pragma snippet "UIQuadStyle"

// Solid styled quad. A quad that asks for backdrop blur draws its tinted fill here;
// UIQuadBatchedBackdrop.fs draws the frosted version when a blur texture is available.
void main()
{
    bool discardFragment;
    vec4 color = XRENGINE_UiShadeStyledQuad(false, vec3(0.0), discardFragment);
    if (discardFragment)
        discard;
    OutColor = color;
}
