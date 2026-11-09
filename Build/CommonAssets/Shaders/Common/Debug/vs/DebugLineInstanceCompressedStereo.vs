#version 460
#extension GL_OVR_multiview2 : require
layout(num_views = 2) in;
#define XR_DEBUG_LINE 1
#define XR_DEBUG_COMPRESSED 1
#include "Common/Debug/helper/DebugInstanceExpansion.glslinc"

uniform mat4 LeftEyeViewProjectionMatrix_VTX;
uniform mat4 RightEyeViewProjectionMatrix_VTX;

void main()
{
    gl_Position = ExpandDebugLine((gl_ViewID_OVR == 0 ? LeftEyeViewProjectionMatrix_VTX : RightEyeViewProjectionMatrix_VTX));
}
