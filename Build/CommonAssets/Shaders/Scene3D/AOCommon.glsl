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

float AODepthToClipZ(float depth)
{
    return ClipDepthRange == 1 ? depth * 2.0f - 1.0f : depth;
}

vec2 AOTextureUVFromClipXY(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5f + 0.5f;
#ifdef XRENGINE_VULKAN
    // Fullscreen AO passes use NDC-derived UVs, while deferred composition
    // samples AO by gl_FragCoord/XRENGINE_ScreenUV. Vulkan's framebuffer
    // origin makes those conventions vertically opposite, so normalize every
    // screen-space AO texture lookup through the same flip.
    uv.y = 1.0f - uv.y;
#else
    if (ClipSpaceYDirection == 1)
        uv.y = 1.0f - uv.y;
#endif
    return uv;
}

vec2 AOTextureUVFromFragPos(vec3 fragPos)
{
    return AOTextureUVFromClipXY(fragPos.xy);
}

bool AOIsFarDepth(float depth)
{
    const float eps = 1e-6f;
    return DepthMode == 1 ? depth <= eps : depth >= 1.0f - eps;
}

vec3 AOViewPosFromDepth(float depth, vec2 uv, mat4 inverseProjMatrix)
{
    vec4 clipSpacePosition = vec4(uv * 2.0f - 1.0f, AODepthToClipZ(depth), 1.0f);
    vec4 viewSpacePosition = inverseProjMatrix * clipSpacePosition;
    return viewSpacePosition.xyz / max(viewSpacePosition.w, 1e-5f);
}

float AOGaussianWeight(float distanceSquared, float sigma)
{
    float safeSigma = max(sigma, 1e-5f);
    return exp(-0.5f * distanceSquared / (safeSigma * safeSigma));
}

#endif
