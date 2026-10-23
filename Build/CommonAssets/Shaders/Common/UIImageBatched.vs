#version 460

layout(location = 0) in vec3 Position;
layout(location = 2) in vec2 TexCoord0;

layout(std430, binding = 0) buffer QuadTransformBuffer { vec4 QuadTransforms[]; };
layout(std430, binding = 1) buffer QuadColorBuffer { vec4 QuadColors[]; };
layout(std430, binding = 2) buffer QuadBoundsBuffer { vec4 QuadBounds[]; };
layout(std430, binding = 3) buffer QuadUvBuffer { vec4 QuadUvs[]; };

uniform mat4 ViewProjectionMatrix_VTX;

layout(location = 4) out vec2 FragUV0;
layout(location = 5) flat out vec4 InstanceColor;

void main()
{
    int id = gl_InstanceID;
    int base4 = id * 4;
    mat4 model = mat4(QuadTransforms[base4], QuadTransforms[base4 + 1],
        QuadTransforms[base4 + 2], QuadTransforms[base4 + 3]);
    vec4 uv = QuadUvs[id];
    FragUV0 = mix(uv.xy, uv.zw, TexCoord0);
    InstanceColor = QuadColors[id];
    gl_Position = ViewProjectionMatrix_VTX * model * vec4(Position, 1.0);
}
