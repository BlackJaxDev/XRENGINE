#version 460
// Reduces the full-resolution depth to the GTAO resolution (one texel per 2x2 block) and
// writes the world normal of the chosen depth sample. GTAO generation and the blur passes
// then read these small targets, which use the texture cache better than wide taps into the
// full-resolution depth. GTAOUpsample.fs uses the same block rule to find the depth of
// each reduced texel again.
#include "AOCommon.glsl"
#include "GTAOHalfDepthCommon.glsl"
#pragma snippet "NormalEncoding"

layout(location = 0) out vec2 OutNormal;
layout(location = 1) out float OutDepth;
layout(location = 0) in vec3 FragPos;

uniform sampler2D DepthView;

uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;

vec3 ViewPositionAtTexel(ivec2 texel, ivec2 fullSize, mat4 inverseProjMatrix)
{
    texel = clamp(texel, ivec2(0), fullSize - 1);
    float depth = texelFetch(DepthView, texel, 0).r;
    vec2 uv = (vec2(texel) + 0.5f) / vec2(fullSize);
    return AOViewPosFromDepth(depth, uv, inverseProjMatrix);
}

// Picks the neighbor on the same surface, so a silhouette does not bend the normal.
vec3 ClosestDifference(vec3 center, ivec2 texel, ivec2 step, ivec2 fullSize, mat4 inverseProjMatrix)
{
    vec3 forward = ViewPositionAtTexel(texel + step, fullSize, inverseProjMatrix) - center;
    vec3 backward = center - ViewPositionAtTexel(texel - step, fullSize, inverseProjMatrix);
    return abs(forward.z) < abs(backward.z) ? forward : backward;
}

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;

    // The pass renders the reduced target with a matching render area, so the fragment
    // position is the reduced texel.
    ivec2 fullSize = textureSize(DepthView, 0).xy;
    ivec2 halfTexel = max(ivec2(gl_FragCoord.xy - ScreenOrigin), ivec2(0));
    vec2 gatherUV = XR_GTAO_HalfBlockGatherUV(halfTexel, fullSize);
    vec4 block = textureGather(DepthView, gatherUV, 0);
    int pick = XR_GTAO_HalfBlockPick(block, halfTexel);
    float depth = block[pick];
    OutDepth = depth;
    if (AOIsFarDepth(depth))
    {
        OutNormal = XRENGINE_EncodeNormal(vec3(0.0f, 0.0f, 1.0f));
        return;
    }

    mat4 inverseProjMatrix = InverseProjMatrix;
    ivec2 texel = XR_GTAO_HalfBlockTexel(halfTexel, pick, fullSize);
    vec3 center = ViewPositionAtTexel(texel, fullSize, inverseProjMatrix);
    vec3 normal = normalize(cross(
        ClosestDifference(center, texel, ivec2(1, 0), fullSize, inverseProjMatrix),
        ClosestDifference(center, texel, ivec2(0, 1), fullSize, inverseProjMatrix)));
    if (dot(normal, center) > 0.0f)
        normal = -normal;

    mat4 inverseViewMatrix = InverseViewMatrix;
    OutNormal = XRENGINE_EncodeNormal(normalize((inverseViewMatrix * vec4(normal, 0.0f)).xyz));
}
