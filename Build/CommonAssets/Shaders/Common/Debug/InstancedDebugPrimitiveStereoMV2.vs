#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

layout(location = 0) out int instanceID;

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
}