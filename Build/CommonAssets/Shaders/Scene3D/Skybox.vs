#version 450

layout(location = 0) in vec3 Position;
layout(location = 0) out vec3 FragClipPos;

void main()
{
    FragClipPos = Position;
    // Output at maximum depth (z=1) so skybox is behind everything
    gl_Position = vec4(Position.xy, 1.0, 1.0);
}
