#version 460
#extension GL_OVR_multiview2 : require
layout(num_views = 2) in;
#define XR_GIZMO_ARROW 0
#include "GizmoScreenSpace.glslinc"

void main()
{
    int corner = InitializeGizmoCorner();
    gl_Position = ExpandGizmoCorner(gl_ViewID_OVR == 0 ? LeftEyeViewProjectionMatrix_VTX : RightEyeViewProjectionMatrix_VTX, corner);
}
