#version 460
#include "AOCommon.glsl"

#pragma snippet "NormalEncoding"

layout(location = 0) out vec2 OutNormal;
layout(location = 0) in vec3 FragPos;

uniform sampler2D DepthView;

uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;

float ReadDepth(vec2 uv)
{
    return texture(DepthView, uv).r;
}

vec3 ViewPosition(vec2 uv)
{
    return AOViewPosFromDepth(ReadDepth(uv), uv, InverseProjMatrix);
}

// Picks the neighbor on the same surface, so a silhouette does not bend the normal.
vec3 ClosestDifference(vec3 center, vec2 uv, vec2 step)
{
    vec3 forward = ViewPosition(uv + step) - center;
    vec3 backward = center - ViewPosition(uv - step);
    return abs(forward.z) < abs(backward.z) ? forward : backward;
}

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);

    if (AOIsFarDepth(ReadDepth(uv)))
    {
        OutNormal = XRENGINE_EncodeNormal(vec3(0.0f, 0.0f, 1.0f));
        return;
    }

    vec2 texelSize = 1.0f / vec2(textureSize(DepthView, 0).xy);
    vec3 center = ViewPosition(uv);
    vec3 normal = normalize(cross(
        ClosestDifference(center, uv, vec2(texelSize.x, 0.0f)),
        ClosestDifference(center, uv, vec2(0.0f, texelSize.y))));
    if (dot(normal, center) > 0.0f)
        normal = -normal;

    OutNormal = XRENGINE_EncodeNormal(normalize((InverseViewMatrix * vec4(normal, 0.0f)).xyz));
}
