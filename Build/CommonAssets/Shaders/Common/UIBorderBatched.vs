#version 460

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 TexCoord0;

layout(std430, binding = 0) buffer QuadTransformBuffer { vec4 QuadTransforms[]; };
layout(std430, binding = 1) buffer QuadColorBuffer { vec4 QuadColors[]; };
layout(std430, binding = 2) buffer QuadBoundsBuffer { vec4 QuadBounds[]; };
layout(std430, binding = 3) buffer QuadUvBuffer { vec4 QuadBorderColors[]; };
layout(std430, binding = 4) buffer QuadBorderWidthBuffer { vec4 QuadBorderWidths[]; };
layout(std430, binding = 5) buffer QuadDrawIndexBuffer { uint QuadDrawIndices[]; };

uniform mat4 ViewProjectionMatrix_VTX;

layout(location = 4) out vec2 FragUV0;
layout(location = 5) flat out vec4 FillColor;
layout(location = 6) flat out vec4 OutlineColor;
layout(location = 7) flat out vec3 BorderSize;

void main()
{
    int id = int(QuadDrawIndices[gl_InstanceID]);
    int base4 = id * 4;
    mat4 model = mat4(QuadTransforms[base4], QuadTransforms[base4 + 1],
        QuadTransforms[base4 + 2], QuadTransforms[base4 + 3]);
    FragUV0 = TexCoord0;
    FillColor = QuadColors[id];
    OutlineColor = QuadBorderColors[id];
    BorderSize = vec3(QuadBounds[id].zw, QuadBorderWidths[id].x);
    gl_Position = ViewProjectionMatrix_VTX * model * vec4(Position, 1.0);
}
