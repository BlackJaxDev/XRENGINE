#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 0) flat out vec4 MatColor;
layout(location = 1) out vec2 FragUV;

struct Point
{
    vec4 position;
    vec4 color;
};

layout(std430, binding = 0) buffer PointsBuffer
{
    Point Points[];
};

out gl_PerVertex
{
	vec4 gl_Position;
	float gl_PointSize;
	float gl_ClipDistance[];
};

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;
uniform float PointSize = 0.01f;

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    int index = instanceID[0];
    Point point = Points[index];
    vec3 center = point.position.xyz;
    
    vec3 right = InverseViewMatrix[0].xyz;
    vec3 up = InverseViewMatrix[1].xyz;
    
    float dist = length(center - InverseViewMatrix[3].xyz);
    right *= dist;
    up *= dist;
    
    // Define quad corners in world space
    vec3 offset1 = (-right - up) * PointSize;
    vec3 offset2 = ( right - up) * PointSize;
    vec3 offset3 = (-right + up) * PointSize;
    vec3 offset4 = ( right + up) * PointSize;
    vec2 uv0 = vec2(-1.0, -1.0);
    vec2 uv1 = vec2( 1.0, -1.0);
    vec2 uv2 = vec2(-1.0,  1.0);
    vec2 uv3 = vec2( 1.0,  1.0);

    MatColor = point.color;

    FragUV = uv0;
    gl_Position = viewProj * vec4(center + offset1, 1.0);
    EmitVertex();

    FragUV = uv1;
    gl_Position = viewProj * vec4(center + offset2, 1.0);
    EmitVertex();

    FragUV = uv2;
    gl_Position = viewProj * vec4(center + offset3, 1.0);
    EmitVertex();

    FragUV = uv3;
    gl_Position = viewProj * vec4(center + offset4, 1.0);
    EmitVertex();
    
    EndPrimitive();
}