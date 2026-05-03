#version 450
layout (triangles) in;
layout (triangle_strip, max_vertices=24) out;

layout (location = 0) in vec3 InFragPos[];

uniform int CascadeLayerCount;
uniform mat4 CascadeViewProjectionMatrices[8];

void main()
{
    int layerCount = clamp(CascadeLayerCount, 0, 8);
    for (int layer = 0; layer < layerCount; ++layer)
    {
        gl_Layer = layer;
        for (int i = 0; i < 3; ++i)
        {
            gl_Position = CascadeViewProjectionMatrices[layer] * vec4(InFragPos[i], 1.0);
            EmitVertex();
        }
        EndPrimitive();
    }
}
