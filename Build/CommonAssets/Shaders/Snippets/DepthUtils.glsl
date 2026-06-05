// Depth Utilities Snippet
// Requires: ProjMatrix (mat4), CameraNearZ (float), CameraFarZ (float), DepthMode (int), ClipDepthRange (int)

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

ivec2 XRENGINE_ScreenPixelLocal(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    return ivec2(floor(XRENGINE_ScreenCoordLocal(fragCoord, screenOrigin, screenSize)));
}

vec2 XRENGINE_ScreenNoiseCoord(vec2 fragCoord, vec2 screenOrigin, vec2 screenSize)
{
    return floor(XRENGINE_ScreenCoordLocal(fragCoord, screenOrigin, screenSize));
}

#endif

float XRENGINE_ResolveDepth(float depth)
{
    return DepthMode == 1 ? (1.0 - depth) : depth;
}

float XRENGINE_DepthToClipZ(float depth)
{
    return ClipDepthRange == 1 ? depth * 2.0 - 1.0 : depth;
}

float XRENGINE_ClipZToDepth(float clipZ)
{
    return ClipDepthRange == 1 ? clipZ * 0.5 + 0.5 : clipZ;
}

vec3 XRENGINE_WorldPosFromDepth(float depth, vec2 uv, mat4 invProj, mat4 cameraToWorld)
{
    float z = XRENGINE_DepthToClipZ(XRENGINE_ResolveDepth(depth));
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, z, 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    viewSpacePosition /= viewSpacePosition.w;
    vec4 worldSpacePosition = cameraToWorld * viewSpacePosition;
    return worldSpacePosition.xyz;
}

// Overload using raw depth (no DepthMode resolve) with explicit matrices.
// Matches the pattern used in deferred lighting / decal passes.
vec3 XRENGINE_WorldPosFromDepthRaw(float depth, vec2 uv, mat4 invProj, mat4 invView)
{
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, XRENGINE_DepthToClipZ(depth), 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    viewSpacePosition /= viewSpacePosition.w;
    return (invView * viewSpacePosition).xyz;
}

vec3 XRENGINE_ViewPosFromDepthRaw(float depth, vec2 uv, mat4 invProj)
{
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, XRENGINE_DepthToClipZ(depth), 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

vec3 XRENGINE_ViewPosFromDepth(float depth, vec2 uv, mat4 invProj)
{
    float z = XRENGINE_DepthToClipZ(XRENGINE_ResolveDepth(depth));
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, z, 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

// Fast view-space position reconstruction for symmetric projections.
// Avoids the full mat4 × vec4 multiply by exploiting the diagonal structure
// of a symmetric projection matrix. Requires precomputed constants:
//   invProjX  = 1.0 / ProjMatrix[0][0]
//   invProjY  = 1.0 / ProjMatrix[1][1]
//   projWScale = InverseProjMatrix[2][3]   (perspective-divide row, ndcZ coefficient)
//   projWBias  = InverseProjMatrix[3][3]   (perspective-divide row, constant term)
vec3 XRENGINE_ViewPosFromDepthFast(float depth, vec2 uv, float invProjX, float invProjY, float projWScale, float projWBias)
{
    float ndcZ = XRENGINE_DepthToClipZ(depth);
    float w = projWScale * ndcZ + projWBias;
    float recipW = 1.0 / max(abs(w), 1e-7) * sign(w);
    vec2 ndcXY = uv * 2.0 - 1.0;
    return vec3(ndcXY.x * invProjX * recipW, ndcXY.y * invProjY * recipW, -recipW);
}

// Overload accepting a projection matrix directly (extracts constants internally).
vec3 XRENGINE_ViewPosFromDepthFast(float depth, vec2 uv, mat4 projMatrix, mat4 inverseProjMatrix)
{
    return XRENGINE_ViewPosFromDepthFast(
        depth, uv,
        1.0 / projMatrix[0][0],
        1.0 / projMatrix[1][1],
        inverseProjMatrix[2][3],
        inverseProjMatrix[3][3]);
}

float XRENGINE_LinearizeDepth(float depth, float nearZ, float farZ)
{
    float depthSample = XRENGINE_ResolveDepth(depth);
    if (ClipDepthRange == 1)
    {
        float clipZ = 2.0 * depthSample - 1.0;
        return 2.0 * nearZ * farZ / (farZ + nearZ - clipZ * (farZ - nearZ));
    }

    return nearZ * farZ / (farZ - depthSample * (farZ - nearZ));
}

float XRENGINE_DepthFromLinear(float linearZ, float nearZ, float farZ)
{
    float depth;
    if (ClipDepthRange == 1)
    {
        float nonLinearDepth = (farZ + nearZ - 2.0 * nearZ * farZ / linearZ) / (farZ - nearZ);
        depth = (nonLinearDepth + 1.0) / 2.0;
    }
    else
    {
        depth = (farZ - (nearZ * farZ / linearZ)) / (farZ - nearZ);
    }

    return DepthMode == 1 ? (1.0 - depth) : depth;
}
