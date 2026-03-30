#version 450

// Input is a single dummy point to trigger geometry shader invocation.
layout(points) in;

// We output triangle strips so we can build quads and triangles.
layout(triangle_strip, max_vertices = 1024) out;

// Structures matching SSBO data layouts.
struct DebugPoint
{
    vec3 pos;
    float pad;  // unused, for alignment
    vec4 color;
};

struct DebugLine
{
    vec3 pos0;
    float pad0;
    vec3 pos1;
    float pad1;
    vec4 color;
    vec4 pad2;
};

struct DebugTriangle
{
    vec3 pos0;
    float pad0;
    vec3 pos1;
    float pad1;
    vec3 pos2;
    float pad2;
    vec4 color;
};

// Bindings for SSBOs.
layout(std430, binding = 0) buffer PointsBuffer
{
    DebugPoint points[];
};
layout(std430, binding = 1) buffer LinesBuffer
{
    DebugLine lines[];
};
layout(std430, binding = 2) buffer TrianglesBuffer
{
    DebugTriangle triangles[];
};

// Uniforms for number of elements.
uniform uint PointCount;
uniform uint LineCount;
uniform uint TriangleCount;

uniform float PointSize = 0.1f;
uniform float LineSize = 0.01f;

// MVP matrix for transforming positions.
uniform mat4 ModelMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 ViewProjectionMatrix;

// Helper: Emit a vertex with transformed position.
void emitDebugVertex(vec3 pos, vec4 col, mat4 mtx)
{
    gl_Position = mtx * vec4(pos, 1.0);
    EmitVertex();
}

void main()
{
    mat4 MVP = ViewProjectionMatrix * ModelMatrix;

    // --- Debug Points as Billboards ---
    // For each point, output a billboard quad (as a triangle strip).
    vec3 rightVec = normalize(vec3(InverseViewMatrix[0].x, InverseViewMatrix[1].x, InverseViewMatrix[2].x)) * PointSize;
    vec3 upVec = normalize(vec3(InverseViewMatrix[0].y, InverseViewMatrix[1].y, InverseViewMatrix[2].y)) * PointSize;
    for (uint i = 0u; i < PointCount; i++)
    {
        vec3 p = points[i].pos;
        vec4 col = points[i].color;
        
        // Emit vertices in triangle strip order: bottom-left, bottom-right, top-left, top-right.
        emitDebugVertex(p - rightVec - upVec, col, MVP); // Bottom-left
        emitDebugVertex(p + rightVec - upVec, col, MVP); // Bottom-right
        emitDebugVertex(p - rightVec + upVec, col, MVP); // Top-left
        emitDebugVertex(p + rightVec + upVec, col, MVP); // Top-right
        EndPrimitive();
    }
    
    // --- Debug Lines ---
    // For each line, output a quad as two triangles.
    // We compute an approximate perpendicular using a fixed up vector.
    for (uint i = 0u; i < LineCount; i++)
    {
        vec3 p0 = lines[i].pos0;
        vec3 p1 = lines[i].pos1;
        vec4 col = lines[i].color;
        
        vec3 dir = normalize(p1 - p0);
        vec3 up = vec3(0.0, 1.0, 0.0);
        // Ensure up is not parallel to the line direction.
        if (abs(dot(dir, up)) > 0.99)
            up = vec3(1.0, 0.0, 0.0);
        vec3 right = normalize(cross(dir, up)) * LineSize;
        
        // First triangle of the quad.
        emitDebugVertex(p0 - right, col, MVP);
        emitDebugVertex(p0 + right, col, MVP);
        emitDebugVertex(p1 - right, col, MVP);
        EndPrimitive();
        
        // Second triangle of the quad.
        emitDebugVertex(p1 - right, col, MVP);
        emitDebugVertex(p0 + right, col, MVP);
        emitDebugVertex(p1 + right, col, MVP);
        EndPrimitive();
    }
    
    // --- Debug Triangles ---
    // For each triangle, output its three vertices.
    for (uint i = 0u; i < TriangleCount; i++)
    {
        vec3 a = triangles[i].pos0;
        vec3 b = triangles[i].pos1;
        vec3 c = triangles[i].pos2;
        vec4 col = triangles[i].color;
        emitDebugVertex(a, col, MVP);
        emitDebugVertex(b, col, MVP);
        emitDebugVertex(c, col, MVP);
        EndPrimitive();
    }
}
