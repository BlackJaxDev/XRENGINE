#version 450

layout (vertices = 3) out;

layout (location = 0) in vec3 InFragPos[];
layout (location = 1) in vec3 InFragNorm[];
layout (location = 4) in vec2 InFragUV0[];

layout (location = 0) out vec3 TcFragPos[];
layout (location = 1) out vec3 TcFragNorm[];
layout (location = 4) out vec2 TcFragUV0[];

uniform int WaterSubdivision;

void main()
{
    TcFragPos[gl_InvocationID] = InFragPos[gl_InvocationID];
    TcFragNorm[gl_InvocationID] = InFragNorm[gl_InvocationID];
    TcFragUV0[gl_InvocationID] = InFragUV0[gl_InvocationID];
    gl_out[gl_InvocationID].gl_Position = gl_in[gl_InvocationID].gl_Position;

    if (gl_InvocationID == 0)
    {
        float tessellation = clamp(float(WaterSubdivision), 1.0, 64.0);
        gl_TessLevelOuter[0] = tessellation;
        gl_TessLevelOuter[1] = tessellation;
        gl_TessLevelOuter[2] = tessellation;
        gl_TessLevelInner[0] = tessellation;
    }
}
