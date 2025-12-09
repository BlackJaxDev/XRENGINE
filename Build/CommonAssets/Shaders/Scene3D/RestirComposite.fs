#version 450 core

layout(location = 0) out vec4 OutColor;

uniform sampler2D RestirGITexture;
uniform float ScreenWidth;
uniform float ScreenHeight;

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
    vec3 gi = texture(RestirGITexture, uv).rgb;
    OutColor = vec4(gi, 0.0);
}
