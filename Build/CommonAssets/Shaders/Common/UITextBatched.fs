#version 460

layout (location = 4) in vec2 FragUV0;
flat in vec4 InstanceTextColor;

layout (location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

void main()
{
    float intensity = texture(Texture0, FragUV0).r;
    FragColor = InstanceTextColor * intensity;
}
