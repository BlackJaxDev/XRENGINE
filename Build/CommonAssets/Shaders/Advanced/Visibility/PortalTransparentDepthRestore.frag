#version 450

// Restores primary scene hardware depth on uncovered pixels inside a transparent-background
// in-place portal or mirror. PortalDepthOnlyReset cleared hardware depth to farDepth so
// reflected geometry could rasterize, leaving primary VisibilityIdentity and VisibilityMetadata
// intact wherever reflected geometry did not draw. Reconstructing primary depth here restores
// accurate shadows, GTAO, clustered lighting, late transparent depth testing, and TSR history.
#include "Advanced/Reconstruction/ReconstructSurface.glslinc"

layout(location = 0) flat in uint PortalViewIndex;

#if defined(XR_ADV_PORTAL_DEPTH_RESTORE_MSAA)
layout(set = 1, binding = 55) uniform usampler2DMSArray RawVisibilityIdentity;
layout(set = 1, binding = 56) uniform usampler2DMSArray RawVisibilityMetadata;
#else
layout(set = 1, binding = 0) uniform usampler2D VisibilityIdentity;
layout(set = 1, binding = 1) uniform usampler2D VisibilityMetadata;
#endif

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    uint primaryView = XR_ADV_ViewMaskIndex(PortalViewIndex);

#if defined(XR_ADV_PORTAL_DEPTH_RESTORE_MSAA)
    ivec2 extent = textureSize(RawVisibilityIdentity).xy;
    if (any(lessThan(pixel, ivec2(0))) || any(greaterThanEqual(pixel, extent)))
        discard;
    ivec3 coord = ivec3(pixel, int(primaryView));
    uvec2 identity = texelFetch(RawVisibilityIdentity, coord, gl_SampleID).rg;
    uint metadata = texelFetch(RawVisibilityMetadata, coord, gl_SampleID).r;
    vec2 sampleCenter = vec2(pixel) + gl_SamplePosition;
#else
    ivec2 extent = textureSize(VisibilityIdentity, 0);
    if (any(lessThan(pixel, ivec2(0))) || any(greaterThanEqual(pixel, extent)))
        discard;
    uvec2 identity = texelFetch(VisibilityIdentity, pixel, 0).rg;
    uint metadata = texelFetch(VisibilityMetadata, pixel, 0).r;
    vec2 sampleCenter = vec2(pixel) + 0.5;
#endif

    // Leave open primary sky at farDepth.
    if (identity.x == 0u ||
        identity.x == XR_ADV_VIS_INVALID ||
        identity.y == XR_ADV_VIS_INVALID ||
        metadata == XR_ADV_VIS_INVALID)
    {
        discard;
    }

    // Leave pixels overwritten by the reflected portal sub-view at their reflected depth.
    uint pixelView = (metadata >> XR_ADV_VIS_VIEW_SHIFT) & 0xFFu;
    if (pixelView != primaryView)
        discard;

    float restoredDepth;
    if (!XR_ADV_TryReconstructPrimaryDepth(identity, metadata, sampleCenter, restoredDepth))
        discard;

    gl_FragDepth = restoredDepth;
}
