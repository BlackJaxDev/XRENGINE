#version 460
#define XR_DEBUG_POINT 1
#define XR_DEBUG_COMPRESSED 1
#include "Common/Debug/helper/DebugInstanceExpansion.glslinc"

uniform mat4 ViewProjectionMatrix_VTX;
uniform mat4 InverseViewMatrix;

void main()
{
    gl_Position = ExpandDebugPoint(ViewProjectionMatrix_VTX, InverseViewMatrix);
}
