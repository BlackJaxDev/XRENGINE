#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 0) flat out vec4 MatColor;

struct Triangle
{
    vec4 p0;
    vec4 p1;
    vec4 p2;
    vec4 color;
};

layout(std430, binding = 0) buffer TrianglesBuffer
{
    Triangle Triangles[];
};

out gl_PerVertex
{
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    int index = instanceID[0];
    Triangle tri = Triangles[index];
    
    MatColor = tri.color;

    gl_Position = viewProj * vec4(tri.p0.xyz, 1.0);
    EmitVertex();
    
    gl_Position = viewProj * vec4(tri.p1.xyz, 1.0);
    EmitVertex();

    gl_Position = viewProj * vec4(tri.p2.xyz, 1.0);
    EmitVertex();
    
    EndPrimitive();
}