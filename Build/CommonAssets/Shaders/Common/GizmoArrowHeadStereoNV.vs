#version 460
#extension GL_NV_viewport_array2 : require
#extension GL_NV_stereo_view_rendering : require
layout(secondary_view_offset = 1) out int gl_Layer;
#define XR_GIZMO_ARROW 1
#include "GizmoScreenSpace.glslinc"

void main()
{
    int corner = InitializeGizmoCorner();
    gl_Position = ExpandGizmoCorner(LeftEyeViewProjectionMatrix_VTX, corner);
    gl_SecondaryPositionNV = ExpandGizmoCorner(RightEyeViewProjectionMatrix_VTX, corner);
    gl_Layer = 0;
}
