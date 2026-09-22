#version 450 core

// Resolve raw MSAA visibility before depth-pyramid, AO, and classification.
// The raw inputs remain arrays for both mono and stereo; the command supplies
// the physical view layer and renders the selected canonical target layer.
#if defined(XR_ADV_BACKEND_VULKAN)
layout(set = 1, binding = 55) uniform usampler2DMSArray RawVisibilityIdentity;
layout(set = 1, binding = 56) uniform usampler2DMSArray RawVisibilityMetadata;
layout(set = 1, binding = 57) uniform usampler2DMSArray RawVisibilitySelection;
layout(set = 1, binding = 58) uniform sampler2DMSArray RawVisibilityDepth;
#else
// Keep GL below its conservative per-stage texture-unit limit. Sampler and
// image namespaces are separate there, so these do not collide with native
// opaque-shading output images.
layout(binding = 5) uniform usampler2DMSArray RawVisibilityIdentity;
layout(binding = 6) uniform usampler2DMSArray RawVisibilityMetadata;
layout(binding = 7) uniform usampler2DMSArray RawVisibilitySelection;
layout(binding = 8) uniform sampler2DMSArray RawVisibilityDepth;
#endif

layout(location = 0) out uvec2 OutVisibilityIdentity;
layout(location = 1) out uint OutVisibilityMetadata;
layout(location = 2) out uint OutVisibilitySelection;

#if defined(XR_ADV_BACKEND_VULKAN)
layout(push_constant, std430) uniform XRAdvancedMsaaResolvePushConstants
{
    uint viewIndex;
    uint reversedDepth;
} XR_ADV_MsaaResolvePush;
#define MultisampleViewIndex XR_ADV_MsaaResolvePush.viewIndex
#define MultisampleReversedDepth (XR_ADV_MsaaResolvePush.reversedDepth != 0u)
#else
uniform uint MultisampleViewIndex;
uniform bool MultisampleReversedDepth;
#endif

const uint XR_ADV_MSAA_VIS_INVALID = 0xFFFFFFFFu;

bool XR_ADV_MSAA_IsCovered(uvec2 identity)
{
    return identity.x != 0u && identity.x != XR_ADV_MSAA_VIS_INVALID;
}

bool XR_ADV_MSAA_IsCloser(float candidate, float current)
{
    return MultisampleReversedDepth ? candidate > current : candidate < current;
}

void main()
{
    ivec2 extent = textureSize(RawVisibilityIdentity).xy;
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    if (any(greaterThanEqual(pixel, extent)))
        discard;
    ivec3 coordinate = ivec3(pixel, int(MultisampleViewIndex));
    float nearestDepth = MultisampleReversedDepth ? 0.0 : 1.0;
    OutVisibilityIdentity = uvec2(XR_ADV_MSAA_VIS_INVALID);
    OutVisibilityMetadata = XR_ADV_MSAA_VIS_INVALID;
    OutVisibilitySelection = XR_ADV_MSAA_VIS_INVALID;
    bool foundCoveredSample = false;

    for (int sampleIndex = 0; sampleIndex < textureSamples(RawVisibilityIdentity); ++sampleIndex)
    {
        uvec2 identity = texelFetch(RawVisibilityIdentity, coordinate, sampleIndex).rg;
        if (!XR_ADV_MSAA_IsCovered(identity))
            continue;

        float depth = texelFetch(RawVisibilityDepth, coordinate, sampleIndex).r;
        if (isnan(depth) || isinf(depth) || depth < 0.0 || depth > 1.0)
            continue;

        if (!foundCoveredSample || XR_ADV_MSAA_IsCloser(depth, nearestDepth))
        {
            foundCoveredSample = true;
            nearestDepth = depth;
            // Each canonical value comes from the same nearest covered sample.
            // That keeps visibility identity, metadata, and editor selection a
            // coherent tuple for later reconstruction and picking.
            OutVisibilityIdentity = identity;
            OutVisibilityMetadata = texelFetch(RawVisibilityMetadata, coordinate, sampleIndex).r;
            OutVisibilitySelection = texelFetch(RawVisibilitySelection, coordinate, sampleIndex).r;
        }
    }

    // The nearest covered sample is conservative for early depth reduction:
    // closest is max under reverse-Z and min under forward-Z. An uncovered
    // pixel retains far depth and invalid visibility for the background pass.
    gl_FragDepth = nearestDepth;
}
