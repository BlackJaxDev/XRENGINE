#version 460
#extension GL_NV_viewport_array2 : require
#extension GL_NV_stereo_view_rendering : require

layout(location = 0) in vec3 Position;
layout(location = 0) out int instanceID;
layout(location = 1) out vec3 vPos;

// NV stereo rendering outputs
out int gl_ViewportMask[1];
out int gl_SecondaryViewportMaskNV[1];

out gl_PerVertex
{
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};

void main()
{
    instanceID = gl_InstanceID;
    vPos = Position;
    gl_Position = vec4(Position, 1.0f);
    gl_SecondaryPositionNV = vec4(Position, 1.0f);
    
    // Set viewport mask for left eye (viewport 0)
    gl_ViewportMask[0] = 1;
    
    // Set viewport mask for right eye (viewport 1)
    gl_SecondaryViewportMaskNV[0] = 2;
}