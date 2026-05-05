#version 450
layout (triangles) in;
layout (triangle_strip, max_vertices=18) out;

layout (location = 0) in vec3 InFragPos[];
layout (location = 1) in vec3 InFragNorm[];
layout (location = 4) in vec2 InFragUV0[];
layout (location = 12) in vec4 InFragColor0[];
layout (location = 20) in vec3 InFragPosLocal[];

uniform int PointShadowFaceCount;
uniform mat4 PointShadowViewProjectionMatrices[6];
uniform int PointShadowFaceIndices[6];

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
layout (location = 12) out vec4 FragColor0;
layout (location = 20) out vec3 FragPosLocal;

void main()
{
    int faceCount = clamp(PointShadowFaceCount, 0, 6);
    for (int faceSlot = 0; faceSlot < faceCount; ++faceSlot)
    {
        gl_ViewportIndex = faceSlot;
        for (int i = 0; i < 3; ++i)
        {
            FragPos = InFragPos[i];
            FragNorm = InFragNorm[i];
            FragUV0 = InFragUV0[i];
            FragColor0 = InFragColor0[i];
            FragPosLocal = InFragPosLocal[i];
            gl_Position = PointShadowViewProjectionMatrices[faceSlot] * vec4(FragPos, 1.0);
            EmitVertex();
        }
        EndPrimitive();
    }
}
