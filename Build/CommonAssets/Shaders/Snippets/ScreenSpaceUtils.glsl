// Screen-space coordinate helpers.
// Engine rectangles and screen-space effects use a bottom-left local coordinate contract.

#ifndef XRENGINE_CLIP_SPACE_Y_DIRECTION_UNIFORM
#define XRENGINE_CLIP_SPACE_Y_DIRECTION_UNIFORM
uniform int ClipSpaceYDirection;
#endif

#ifndef XRENGINE_SCREEN_SPACE_UTILS
#define XRENGINE_SCREEN_SPACE_UTILS

vec2 XRENGINE_ScreenCoordLocal(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    vec2 local = fragCoord - screenOrigin;
    if (ClipSpaceYDirection == 1)
        local.y = screenSize.y - local.y;
    return local;
}

vec2 XRENGINE_ScreenUV(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    return XRENGINE_ScreenCoordLocal(fragCoord, screenOrigin, screenSize) / max(screenSize, vec2(1.0));
}

vec2 XRENGINE_ScreenUV(vec2 fragCoord, vec2 screenSize)
{
    return XRENGINE_ScreenUV(fragCoord, vec2(0.0), screenSize);
}

vec2 XRENGINE_ClipXYToScreenUV(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5 + 0.5;
    if (ClipSpaceYDirection == 1)
        uv.y = 1.0 - uv.y;
    return uv;
}

vec2 XRENGINE_FramebufferCoordLocal(vec2 fragCoord, vec2 screenOrigin)
{
    return fragCoord - screenOrigin;
}

vec2 XRENGINE_FramebufferUV(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    return XRENGINE_FramebufferCoordLocal(fragCoord, screenOrigin) / max(screenSize, vec2(1.0));
}

vec2 XRENGINE_FramebufferUV(vec2 fragCoord, vec2 screenSize)
{
    return XRENGINE_FramebufferUV(fragCoord, vec2(0.0), screenSize);
}

ivec2 XRENGINE_FramebufferPixelLocal(vec2 fragCoord, vec2 screenOrigin)
{
    return ivec2(floor(XRENGINE_FramebufferCoordLocal(fragCoord, screenOrigin)));
}

ivec2 XRENGINE_ScreenPixelLocal(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    return ivec2(floor(XRENGINE_ScreenCoordLocal(fragCoord, screenOrigin, screenSize)));
}

vec2 XRENGINE_ScreenNoiseCoord(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    return floor(XRENGINE_ScreenCoordLocal(fragCoord, screenOrigin, screenSize));
}

#endif
