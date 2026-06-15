#ifndef XRENGINE_AO_COMMON
#define XRENGINE_AO_COMMON

#ifndef XRENGINE_DEPTH_MODE_UNIFORM
#define XRENGINE_DEPTH_MODE_UNIFORM
uniform int DepthMode;
#endif

#ifndef XRENGINE_CLIP_DEPTH_RANGE_UNIFORM
#define XRENGINE_CLIP_DEPTH_RANGE_UNIFORM
uniform int ClipDepthRange;
#endif

#ifndef XRENGINE_CLIP_SPACE_Y_DIRECTION_UNIFORM
#define XRENGINE_CLIP_SPACE_Y_DIRECTION_UNIFORM
uniform int ClipSpaceYDirection;
#endif

#ifndef XRENGINE_FRAMEBUFFER_TEXTURE_Y_DIRECTION_UNIFORM
#define XRENGINE_FRAMEBUFFER_TEXTURE_Y_DIRECTION_UNIFORM
uniform int FramebufferTextureYDirection;
#endif

#ifndef XRENGINE_AO_SCREEN_DIMENSIONS_UNIFORM
#define XRENGINE_AO_SCREEN_DIMENSIONS_UNIFORM
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;
#endif

float AODepthToClipZ(float depth)
{
    return ClipDepthRange == 1 ? depth * 2.0f - 1.0f : depth;
}

vec2 AOTextureUVFromClipXY(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5f + 0.5f;
    if (FramebufferTextureYDirection == 1)
        uv.y = 1.0f - uv.y;
    return uv;
}

vec2 AOClipXYFromTextureUV(vec2 uv)
{
    if (FramebufferTextureYDirection == 1)
        uv.y = 1.0f - uv.y;
    return uv * 2.0f - 1.0f;
}

vec2 AOTextureUVFromFragPos(vec3 fragPos)
{
    vec2 screenSize = vec2(ScreenWidth, ScreenHeight);
    if (screenSize.x > 0.0f && screenSize.y > 0.0f)
        return clamp((gl_FragCoord.xy - ScreenOrigin) / screenSize, vec2(0.0f), vec2(1.0f));

    return AOTextureUVFromClipXY(fragPos.xy);
}

bool AOIsFarDepth(float depth)
{
    const float eps = 1e-6f;
    return DepthMode == 1 ? depth <= eps : depth >= 1.0f - eps;
}

vec3 AOViewPosFromDepth(float depth, vec2 uv, mat4 inverseProjMatrix)
{
    vec4 clipSpacePosition = vec4(AOClipXYFromTextureUV(uv), AODepthToClipZ(depth), 1.0f);
    vec4 viewSpacePosition = inverseProjMatrix * clipSpacePosition;
    return viewSpacePosition.xyz / max(viewSpacePosition.w, 1e-5f);
}

float AOGaussianWeight(float distanceSquared, float sigma)
{
    float safeSigma = max(sigma, 1e-5f);
    return exp(-0.5f * distanceSquared / (safeSigma * safeSigma));
}

#endif
