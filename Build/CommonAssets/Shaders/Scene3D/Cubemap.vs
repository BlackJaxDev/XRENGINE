#version 450

layout(location = 0) in vec3 Position;
layout(location = 20) out vec3 FragPosLocal;

uniform mat4 ModelMatrix;
uniform mat4 ViewProjectionMatrix_VTX;

void main()
{
    FragPosLocal = Position;
    gl_Position = ViewProjectionMatrix_VTX * ModelMatrix * vec4(Position, 1.0);
}
