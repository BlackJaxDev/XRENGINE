#version 450

layout(location = 0) flat in uint PortalViewIndex;

#define XR_ADV_VISIBILITY_COUNTER_INDEX PortalViewIndex
#include "VisibilityInterface.glslinc"

layout(location = 0) out uvec2 OutVisibilityIdentity;
layout(location = 1) out uint OutVisibilityMetadata;
layout(location = 2) out uint OutVisibilitySelection;
layout(location = 3) out vec2 OutVisibilitySamplePosition;

// Resets portal pixels to uncovered background while stamping PortalViewIndex
// into OutVisibilityMetadata so uncovered sky pixels inside the mirror retain their
// reflected view identity.
void main()
{
    OutVisibilityIdentity = uvec2(0u, XR_ADV_VIS_INVALID);
    OutVisibilityMetadata = XR_ADV_PackVisibilityMetadata(
        0u,
        0u,
        false,
        true,
        false,
        PortalViewIndex,
        false,
        0u);
    OutVisibilitySelection = XR_ADV_VIS_INVALID;
    OutVisibilitySamplePosition = gl_SamplePosition;
}
