#version 450 core

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;

uniform sampler2D DDGITexture;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int uDebugMode;

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight));
    vec4 gi = texture(DDGITexture, uv);

    OutColor = vec4(gi.rgb, 0.0);
}
