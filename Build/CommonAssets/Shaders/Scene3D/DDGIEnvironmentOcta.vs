#version 450

#pragma snippet "OctahedralMapping"

layout(location = 1) out vec3 FragWorldDir;

uniform float SkyboxRotation = 0.0;

void main()
{
    int vertexId = gl_VertexID % 3;
    vec2 clipXY = vec2((vertexId << 1) & 2, vertexId & 2) * 2.0 - 1.0;
    vec3 direction = XRENGINE_DecodeOcta(clipXY * 0.5 + 0.5);
    float cosRotation = cos(SkyboxRotation);
    float sinRotation = sin(SkyboxRotation);
    FragWorldDir = vec3(
        direction.x * cosRotation - direction.z * sinRotation,
        direction.y,
        direction.x * sinRotation + direction.z * cosRotation);
    gl_Position = vec4(clipXY, 0.0, 1.0);
}
