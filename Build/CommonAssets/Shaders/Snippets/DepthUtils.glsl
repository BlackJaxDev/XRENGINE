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

vec3 XRENGINE_ViewPosFromDepth(float depth, vec2 uv, mat4 invProj)
{
    float z = XRENGINE_ResolveDepth(depth) * 2.0 - 1.0;
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, z, 1.0);
    vec4 viewSpacePosition = invProj * clipSpacePosition;
    return viewSpacePosition.xyz / viewSpacePosition.w;
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
