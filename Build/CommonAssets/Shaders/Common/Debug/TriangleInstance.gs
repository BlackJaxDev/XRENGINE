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

    // Compute camera position from the inverse view matrix (translation component)
    vec3 cameraPos = InverseViewMatrix[3].xyz;
    
    // Compute the centroid of the triangle
    vec3 centroid = (tri.p0.xyz + tri.p1.xyz + tri.p2.xyz) / 3.0;
    
    // Compute distance from the camera to the centroid
    float distanceToCamera = length(centroid - cameraPos);
    
    // Define a scale factor based on the distance (adjust the multiplier as needed)
    float scaleFactor = distanceToCamera;
    
    // Scale each vertex relative to the centroid
    vec3 scaledP0 = centroid + (tri.p0.xyz - centroid) * scaleFactor;
    vec3 scaledP1 = centroid + (tri.p1.xyz - centroid) * scaleFactor;
    vec3 scaledP2 = centroid + (tri.p2.xyz - centroid) * scaleFactor;
    
    // Update triangle vertex positions with scaled values
    tri.p0 = vec4(scaledP0, tri.p0.w);
    tri.p1 = vec4(scaledP1, tri.p1.w);
    tri.p2 = vec4(scaledP2, tri.p2.w);

    gl_Position = viewProj * vec4(tri.p0.xyz, 1.0);
    EmitVertex();
    
    gl_Position = viewProj * vec4(tri.p1.xyz, 1.0);
    EmitVertex();

    gl_Position = viewProj * vec4(tri.p2.xyz, 1.0);
    EmitVertex();
    
    EndPrimitive();
}