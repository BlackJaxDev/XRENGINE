
#version 460

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;

uniform sampler2D Texture0;
uniform float ScreenWidth;
uniform float ScreenHeight;

void main()
{
    vec2 screenUV = XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight));
    OutColor = texture(Texture0, screenUV);
}
