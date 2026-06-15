#version 450 core

#pragma snippet "HashColor"
layout(location = 0) out vec4 OutColor;

uniform usampler2D TransformId;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int FramebufferTextureYDirection;

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = gl_FragCoord.xy / max(vec2(ScreenWidth, ScreenHeight), vec2(1.0));
    if (FramebufferTextureYDirection == 1)
        uv.y = 1.0 - uv.y;

    uint id = texture(TransformId, uv).r;

    if (id == 0u)
    {
        OutColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    OutColor = vec4(XRENGINE_HashColor(id), 1.0);
}
