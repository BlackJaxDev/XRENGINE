#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;

layout (location = 4) in vec2 FragUV0;

uniform sampler2DArray Texture0;

void main()
{
    vec2 uv = FragUV0;
    vec3 uvi = vec3(uv, gl_ViewID_OVR);
    OutColor = texture(Texture0, uvi);
}
