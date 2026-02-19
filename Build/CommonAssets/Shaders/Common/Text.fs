#version 460

layout(location = 4) in vec2 FragUV0;

layout(location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

uniform vec4 TextColor;

void main()
{
    float intensity = texture(Texture0, FragUV0).r;
    vec4 color = TextColor * intensity; //Pre-multiply alpha
    color.a *= intensity;
    FragColor = color;
}