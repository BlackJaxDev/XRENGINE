#version 450

layout(location = 0) out vec3 FragPos;

void main()
{
    int vertexId = gl_VertexIndex % 3;
    vec2 clipXY = vec2((vertexId << 1) & 2, vertexId & 2) * 2.0 - 1.0;
    FragPos = vec3(clipXY, 0.0);
    gl_Position = vec4(clipXY, 0.0, 1.0);
}
