#version 460

layout(location = 0) flat out int instanceID;
layout(location = 1) out vec3 vPos;

// Minimal passthrough: consume Position at location 0 so VAO bindings are satisfied
layout(location = 0) in vec3 Position;

void main()
{
	instanceID = gl_InstanceID;
	vPos = Position;
	gl_Position = vec4(Position, 1.0f);
	gl_PointSize = 1.0f;
}
