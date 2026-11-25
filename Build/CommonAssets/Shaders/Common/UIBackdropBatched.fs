#version 460
#pragma snippet "ScreenSpaceUtils"

layout(location = 5) flat in vec4 InstanceColor;
layout(location = 0) out vec4 OutColor;

uniform sampler2D Texture0;
uniform vec2 CaptureOrigin;
uniform vec2 CaptureSize;
uniform float BlurStrength;

const vec2 TapOffsets[12] = vec2[](
    vec2(-1, 0), vec2(1, 0), vec2(0, -1), vec2(0, 1),
    vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(1, 1),
    vec2(-2, 0), vec2(2, 0), vec2(0, -2), vec2(0, 2));
const float TapWeights[12] = float[](
    0.10909091, 0.10909091, 0.10909091, 0.10909091,
    0.07272727, 0.07272727, 0.07272727, 0.07272727,
    0.02727273, 0.02727273, 0.02727273, 0.02727273);

void main()
{
    vec2 extent = vec2(textureSize(Texture0, 0));
    vec2 uv = XRENGINE_FramebufferUV(gl_FragCoord.xy, CaptureOrigin, CaptureSize);
    vec2 lower = 0.5 / extent;
    vec2 upper = vec2(1.0) - lower;
    uv = clamp(uv, lower, upper);
    vec3 color = texture(Texture0, uv).rgb * 0.16363636;
    float weight = 0.16363636;
    vec2 stepUV = vec2(BlurStrength) / extent;
    for (int i = 0; i < 12; ++i)
    {
        color += texture(Texture0, clamp(uv + TapOffsets[i] * stepUV, lower, upper)).rgb * TapWeights[i];
        weight += TapWeights[i];
    }
    OutColor = vec4(color / weight, 1.0) * InstanceColor;
}
