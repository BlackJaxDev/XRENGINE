#version 450
layout (triangles) in;
layout (triangle_strip, max_vertices=18) out;

// World-space position from the vertex shader (auto-generated or custom).
// The auto-generated VS always outputs FragPos at location 0 in world space.
layout (location = 0) in vec3 InFragPos[];

uniform mat4 ViewProjectionMatrices[6];

layout (location = 0) out vec3 FragPos;

void main()
{
	for (int face = 0; face < 6; ++face)
	{
		gl_Layer = face;
		for (int i = 0; i < 3; ++i)
		{
			FragPos = InFragPos[i];
			gl_Position = ViewProjectionMatrices[face] * vec4(FragPos, 1.0);
			EmitVertex();
		}    
		EndPrimitive();
	}
} 
