uniform sampler2D PrevPeelDepth;
#ifndef XRENGINE_SCREEN_SIZE_UNIFORMS
#define XRENGINE_SCREEN_SIZE_UNIFORMS
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif
uniform int DepthPeelLayerIndex;
uniform float DepthPeelEpsilon;

bool XRE_ShouldDiscardDepthPeelFragment()
{
    if (DepthPeelLayerIndex <= 0)
        return false;

    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
    float previousDepth = texture(PrevPeelDepth, uv).r;
    return gl_FragCoord.z <= previousDepth + DepthPeelEpsilon;
}
