#version 460
#define XR_DEBUG_TRIANGLE 1
#define XR_DEBUG_COMPRESSED 1
#include "Common/Debug/helper/DebugInstanceExpansion.glslinc"

uniform mat4 ViewProjectionMatrix_VTX;

void main()
{
    gl_Position = ExpandDebugTriangle(ViewProjectionMatrix_VTX);
}
