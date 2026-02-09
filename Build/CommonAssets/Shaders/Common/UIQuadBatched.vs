#version 460

layout (location = 0) in vec3 Position;
layout (location = 1) in vec3 Normal;
layout (location = 2) in vec2 TexCoord0;

// Per-instance data stored in SSBOs
// Model matrices stored as 4 consecutive vec4 rows per instance
layout(std430, binding = 0) buffer QuadTransformBuffer
{
    vec4 QuadTransforms[]; // 4 vec4s per instance (row-major mat4)
};

layout(std430, binding = 1) buffer QuadColorBuffer
{
    vec4 QuadColors[];
};

layout(std430, binding = 2) buffer QuadBoundsBuffer
{
    vec4 QuadBounds[]; // (x, y, w, h) per instance
};

uniform mat4 InverseViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;

layout (location = 0) out vec3 FragPos;
layout (location = 1) out vec3 FragNorm;
layout (location = 4) out vec2 FragUV0;
flat out vec4 InstanceColor;
flat out vec4 InstanceBounds;

out gl_PerVertex
{
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};

mat4 getModelMatrix(int instanceID)
{
    int base4 = instanceID * 4;
    // C# Matrix4x4 is row-major in memory, GLSL mat4 is column-major
    // Read rows and transpose to get correct column-major mat4
    vec4 row0 = QuadTransforms[base4 + 0];
    vec4 row1 = QuadTransforms[base4 + 1];
    vec4 row2 = QuadTransforms[base4 + 2];
    vec4 row3 = QuadTransforms[base4 + 3];
    return mat4(
        vec4(row0.x, row1.x, row2.x, row3.x),
        vec4(row0.y, row1.y, row2.y, row3.y),
        vec4(row0.z, row1.z, row2.z, row3.z),
        vec4(row0.w, row1.w, row2.w, row3.w)
    );
}

void main()
{
    mat4 modelMatrix = getModelMatrix(gl_InstanceID);
    vec4 color = QuadColors[gl_InstanceID];
    vec4 bounds = QuadBounds[gl_InstanceID];

    mat4 ViewMatrix = inverse(InverseViewMatrix_VTX);
    mat4 mvpMatrix = ProjMatrix_VTX * ViewMatrix * modelMatrix;

    vec4 position = vec4(Position, 1.0);

    FragPos = (mvpMatrix * position).xyz;
    gl_Position = mvpMatrix * position;
    FragNorm = Normal;
    FragUV0 = TexCoord0;
    InstanceColor = color;
    InstanceBounds = bounds;
}
