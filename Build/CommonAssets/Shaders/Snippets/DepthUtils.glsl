// Depth Utilities Snippet
// Requires: ProjMatrix (mat4), CameraNearZ (float), CameraFarZ (float), DepthMode (int)

uniform int DepthMode;

float XRENGINE_ResolveDepth(float depth)
{
    return DepthMode == 1 ? (1.0 - depth) : depth;
}

vec3 XRENGINE_WorldPosFromDepth(float depth, vec2 uv, mat4 invProj, mat4 cameraToWorld)
{
    float z = XRENGINE_ResolveDepth(depth) * 2.0 - 1.0;
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
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0 - 1.0, 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    viewSpacePosition /= viewSpacePosition.w;
    return (invView * viewSpacePosition).xyz;
}

vec3 XRENGINE_ViewPosFromDepth(float depth, vec2 uv, mat4 invProj)
{
    float z = XRENGINE_ResolveDepth(depth) * 2.0 - 1.0;
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, z, 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

// Fast view-space position reconstruction for symmetric projections.
// Avoids the full mat4 × vec4 multiply by exploiting the diagonal structure
// of a symmetric projection matrix. Requires precomputed constants:
//   invProjX = 1.0 / ProjMatrix[0][0]
//   invProjY = 1.0 / ProjMatrix[1][1]
//   projZScale = InverseProjMatrix[2][2]
//   projZBias  = InverseProjMatrix[3][2]
vec3 XRENGINE_ViewPosFromDepthFast(float depth, vec2 uv, float invProjX, float invProjY, float projZScale, float projZBias)
{
    float ndcZ = depth * 2.0 - 1.0;
    float viewZ = projZBias / max(ndcZ - projZScale, 1e-7);
    vec2 ndcXY = uv * 2.0 - 1.0;
    return vec3(ndcXY.x * invProjX * viewZ, ndcXY.y * invProjY * viewZ, viewZ);
}

// Overload accepting a projection matrix directly (extracts constants internally).
vec3 XRENGINE_ViewPosFromDepthFast(float depth, vec2 uv, mat4 projMatrix, mat4 inverseProjMatrix)
{
    return XRENGINE_ViewPosFromDepthFast(
        depth, uv,
        1.0 / projMatrix[0][0],
        1.0 / projMatrix[1][1],
        inverseProjMatrix[2][2],
        inverseProjMatrix[3][2]);
}

float XRENGINE_LinearizeDepth(float depth, float nearZ, float farZ)
{
    float depthSample = 2.0 * XRENGINE_ResolveDepth(depth) - 1.0;
    return 2.0 * nearZ * farZ / (farZ + nearZ - depthSample * (farZ - nearZ));
}

float XRENGINE_DepthFromLinear(float linearZ, float nearZ, float farZ)
{
    float nonLinearDepth = (farZ + nearZ - 2.0 * nearZ * farZ / linearZ) / (farZ - nearZ);
    float depth = (nonLinearDepth + 1.0) / 2.0;
    return DepthMode == 1 ? (1.0 - depth) : depth;
}
