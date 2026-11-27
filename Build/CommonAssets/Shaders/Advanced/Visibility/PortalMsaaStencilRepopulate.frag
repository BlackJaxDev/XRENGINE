#version 450 core

// Repopulates single-sample canonical stencil portal node bits (bits 2..6, mask 0x7C)
// inside the MultisampleResolve render pass from the resolved raw MSAA visibility
// view index. Uses Vulkan 1.0 core stencil replacement with dynamic StencilReference
// so no VK_EXT_shader_stencil_export extension is required.
layout(set = 1, binding = 55) uniform usampler2DMSArray RawVisibilityIdentity;
layout(set = 1, binding = 56) uniform usampler2DMSArray RawVisibilityMetadata;
layout(set = 1, binding = 58) uniform sampler2DMSArray RawVisibilityDepth;

layout(push_constant, std430) uniform XRAdvancedMsaaPortalStencilPushConstants
{
    uint viewIndex;
    uint reversedDepth;
    uint targetPortalViewIndex;
} XR_ADV_MsaaPortalStencilPush;

const uint XR_ADV_MSAA_VIS_INVALID = 0xFFFFFFFFu;
const uint XR_ADV_VIS_VIEW_SHIFT = 8u;

bool XR_ADV_MSAA_IsCovered(uvec2 identity)
{
    return identity.x != 0u && identity.x != XR_ADV_MSAA_VIS_INVALID;
}

bool XR_ADV_MSAA_IsCloser(float candidate, float current, bool reversedDepth)
{
    return reversedDepth ? candidate > current : candidate < current;
}

void main()
{
    ivec2 extent = textureSize(RawVisibilityIdentity).xy;
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    if (any(greaterThanEqual(pixel, extent)))
        discard;

    bool reversedDepth = XR_ADV_MsaaPortalStencilPush.reversedDepth != 0u;
    ivec3 coordinate = ivec3(pixel, int(XR_ADV_MsaaPortalStencilPush.viewIndex));
    float nearestDepth = reversedDepth ? 0.0 : 1.0;
    uint resolvedMetadata = XR_ADV_MSAA_VIS_INVALID;
    bool foundCoveredSample = false;

    int sampleCount = textureSamples(RawVisibilityIdentity);
    for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex)
    {
        uvec2 identity = texelFetch(RawVisibilityIdentity, coordinate, sampleIndex).rg;
        if (!XR_ADV_MSAA_IsCovered(identity))
            continue;

        float depth = texelFetch(RawVisibilityDepth, coordinate, sampleIndex).r;
        if (isnan(depth) || isinf(depth) || depth < 0.0 || depth > 1.0)
            continue;

        if (!foundCoveredSample || XR_ADV_MSAA_IsCloser(depth, nearestDepth, reversedDepth))
        {
            foundCoveredSample = true;
            nearestDepth = depth;
            resolvedMetadata = texelFetch(RawVisibilityMetadata, coordinate, sampleIndex).r;
        }
    }

    if (!foundCoveredSample && sampleCount > 0)
        resolvedMetadata = texelFetch(RawVisibilityMetadata, coordinate, 0).r;

    if (resolvedMetadata == XR_ADV_MSAA_VIS_INVALID)
        discard;

    uint resolvedViewIndex = (resolvedMetadata >> XR_ADV_VIS_VIEW_SHIFT) & 0xFFu;
    if (resolvedViewIndex != XR_ADV_MsaaPortalStencilPush.targetPortalViewIndex)
        discard;
}
