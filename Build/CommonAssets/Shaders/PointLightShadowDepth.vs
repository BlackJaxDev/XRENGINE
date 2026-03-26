#version 450

layout (location = 0) in vec3 Position;

uniform mat4 ModelMatrix;

void main()
{
	// Output world-space position for the geometry shader.
	// The GS will project each vertex into the 6 cubemap face clip spaces.
	gl_Position = ModelMatrix * vec4(Position, 1.0);
}
