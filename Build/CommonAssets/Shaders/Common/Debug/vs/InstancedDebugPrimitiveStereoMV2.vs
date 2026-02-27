#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

layout(location = 0) in vec3 Position;
layout(location = 0) out int instanceID;
layout(location = 1) out vec3 vPos;

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
}