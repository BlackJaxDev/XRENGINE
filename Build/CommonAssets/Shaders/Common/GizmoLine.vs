#version 460
#define XR_GIZMO_ARROW 0
#include "GizmoScreenSpace.glslinc"

void main()
{
    int corner = InitializeGizmoCorner();
    gl_Position = ExpandGizmoCorner(ViewProjectionMatrix_VTX, corner);
}
