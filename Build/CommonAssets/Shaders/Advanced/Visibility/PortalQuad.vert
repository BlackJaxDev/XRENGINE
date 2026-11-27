#version 450
#if defined(XR_ADV_MULTIVIEW_RASTER)
#extension GL_EXT_multiview : require
#endif

// Draws the four world-space corners of a scheduled planar mirror or portal quad
// without needing a mesh vertex buffer (4 vertices via TriangleStrip).
// Push constants supply the 4 corners, the projection view index, the stamped portal
// view index, and the far-depth value (0.0 for reversed-Z, 1.0 for standard-Z).
layout(push_constant, std430) uniform XRAdvancedPortalQuadPushConstants
{
    vec4 corner0AndParentView;  // xyz = corner 0 WS, w = floatBitsToUint(parentViewIndex)
    vec4 corner1AndPortalView;  // xyz = corner 1 WS, w = floatBitsToUint(portalViewIndex)
    vec4 corner2AndFarDepth;    // xyz = corner 2 WS, w = farDepth (0.0 reversed-Z, 1.0 standard-Z)
    vec4 corner3AndFlags;       // xyz = corner 3 WS, w = floatBitsToUint(flags: bit 0 = forceFarDepth)
} XR_ADV_PortalQuadPush;

#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "VisibilityInterface.glslinc"

layout(location = 0) flat out uint PortalViewIndex;

void main()
{
    uint parentViewIndex = floatBitsToUint(XR_ADV_PortalQuadPush.corner0AndParentView.w);
    uint portalViewIndex = floatBitsToUint(XR_ADV_PortalQuadPush.corner1AndPortalView.w);
    uint flags = floatBitsToUint(XR_ADV_PortalQuadPush.corner3AndFlags.w);
    PortalViewIndex = portalViewIndex;

#if defined(XR_ADV_MULTIVIEW_RASTER)
    uint expectedLayer = XR_ADV_ViewMaskIndex(portalViewIndex);
    if (uint(gl_ViewIndex) != expectedLayer)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }
#endif

    // Depth-reset mode draws a fullscreen triangle at far depth restricted by
    // the node scissor rectangle and stencil equality test.
    if ((flags & 1u) != 0u)
    {
        vec2 ndc = gl_VertexIndex == 0 ? vec2(-1.0, -1.0)
            : gl_VertexIndex == 1 ? vec2(3.0, -1.0)
            : vec2(-1.0, 3.0);
        float farDepth = XR_ADV_PortalQuadPush.corner2AndFarDepth.w;
        gl_Position = vec4(ndc, farDepth, 1.0);
        return;
    }

    if (parentViewIndex >= uint(XR_ADV_Views.records.length()))
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    vec3 corner = gl_VertexIndex == 0 ? XR_ADV_PortalQuadPush.corner0AndParentView.xyz
        : gl_VertexIndex == 1 ? XR_ADV_PortalQuadPush.corner1AndPortalView.xyz
        : gl_VertexIndex == 2 ? XR_ADV_PortalQuadPush.corner3AndFlags.xyz
        : XR_ADV_PortalQuadPush.corner2AndFarDepth.xyz;

    XRAdvancedViewRecord parentView = XR_ADV_LoadView(parentViewIndex);
    gl_Position = vec4(corner, 1.0) * parentView.viewProjectionJittered;
}
