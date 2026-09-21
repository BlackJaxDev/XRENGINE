#version 450 core
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;

uniform sampler2DArray DDGITexture;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform int ViewIndex; // 0 = left eye, 1 = right eye
uniform int uDebugMode;

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    int eye = ViewIndex;
#ifdef GL_OVR_multiview2
    eye = int(gl_ViewID_OVR);
#endif

    vec2 uv = XRENGINE_ScreenUV(gl_FragCoord.xy, vec2(ScreenWidth, ScreenHeight));
    vec4 gi = texture(DDGITexture, vec3(uv, float(eye)));

    OutColor = vec4(gi.rgb, 0.0);
}
