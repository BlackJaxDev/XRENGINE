#version 460
#extension GL_NV_viewport_array2 : require
#extension GL_NV_stereo_view_rendering : require

layout(location = 0) out int instanceID;

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

    // No vertex attributes needed
    gl_Position = vec4(0.0f);
    gl_SecondaryPositionNV = vec4(0.0f);
    
    // Set viewport mask for left eye (viewport 0)
    gl_ViewportMask[0] = 1;
    
    // Set viewport mask for right eye (viewport 1)
    gl_SecondaryViewportMaskNV[0] = 2;
}