#version 450

layout(location = 0) in vec3 Position;
layout(location = 0) out vec3 FragPos;

void main()
{
    FragPos = Position;
    gl_Position = vec4(Position.xy, 0.0f, 1.0f);
}
