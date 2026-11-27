#version 450
#if defined(XR_ADV_MULTIVIEW_RASTER)
#extension GL_EXT_multiview : require
#endif

// Draws the 4-vertex mirror glass composite quad using the parent view's jittered
// projection matrix during deepest-out portal unwind. Hardware depth write restores
// real mirror plane depth and StencilOp.Invert unwinds bits 2..6 to parentNodeId.
layout(push_constant, std430) uniform XRAdvancedPortalGlassPushConstants
{
    vec4 corner0AndParentView;   // xyz = corner 0 WS, w = uintBitsToFloat(parentViewIndex)
    vec4 corner1AndFresnel;      // xyz = corner 1 WS, w = fresnelStrength
    vec4 corner2AndSpecular;     // xyz = corner 2 WS, w = specularStrength
    vec4 corner3AndPortalView;   // xyz = corner 3 WS, w = uintBitsToFloat(portalViewIndex)
    vec4 glassTint;              // rgb = tint color, a = glass tint opacity
    vec4 planeNormalAndUnused;   // xyz = world plane normal, w = unused
} XR_ADV_PortalGlassPush;

#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "VisibilityInterface.glslinc"

layout(location = 0) out vec3 OutWorldPosition;
layout(location = 1) flat out vec3 OutParentCameraPos;
layout(location = 2) flat out vec3 OutPlaneNormal;
layout(location = 3) flat out vec4 OutGlassTint;
layout(location = 4) flat out vec2 OutFresnelAndSpecular;

void main()
{
    uint parentViewIndex = floatBitsToUint(XR_ADV_PortalGlassPush.corner0AndParentView.w);
    uint portalViewIndex = floatBitsToUint(XR_ADV_PortalGlassPush.corner3AndPortalView.w);

#if defined(XR_ADV_MULTIVIEW_RASTER)
    uint expectedLayer = XR_ADV_ViewMaskIndex(portalViewIndex);
    if (uint(gl_ViewIndex) != expectedLayer)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        OutWorldPosition = vec3(0.0);
        OutParentCameraPos = vec3(0.0);
        OutPlaneNormal = vec3(0.0, 0.0, 1.0);
        OutGlassTint = vec4(0.0);
        OutFresnelAndSpecular = vec2(0.0);
        return;
    }
#endif

    if (parentViewIndex >= uint(XR_ADV_Views.records.length()))
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        OutWorldPosition = vec3(0.0);
        OutParentCameraPos = vec3(0.0);
        OutPlaneNormal = vec3(0.0, 0.0, 1.0);
        OutGlassTint = vec4(0.0);
        OutFresnelAndSpecular = vec2(0.0);
        return;
    }

    vec3 corner = gl_VertexIndex == 0 ? XR_ADV_PortalGlassPush.corner0AndParentView.xyz
        : gl_VertexIndex == 1 ? XR_ADV_PortalGlassPush.corner1AndFresnel.xyz
        : gl_VertexIndex == 2 ? XR_ADV_PortalGlassPush.corner3AndPortalView.xyz
        : XR_ADV_PortalGlassPush.corner2AndSpecular.xyz;

    XRAdvancedViewRecord parentView = XR_ADV_LoadView(parentViewIndex);
    gl_Position = vec4(corner, 1.0) * parentView.viewProjectionJittered;
    OutWorldPosition = corner;
    OutParentCameraPos = parentView.cameraPositionAndNear.xyz;
    OutPlaneNormal = XR_ADV_PortalGlassPush.planeNormalAndUnused.xyz;
    OutGlassTint = XR_ADV_PortalGlassPush.glassTint;
    OutFresnelAndSpecular = vec2(
        XR_ADV_PortalGlassPush.corner1AndFresnel.w,
        XR_ADV_PortalGlassPush.corner2AndSpecular.w);
}
