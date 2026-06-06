#version 450 core

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

layout(binding = 0) uniform sampler2DMS DepthView;
uniform bool IsReversedDepth;

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    ivec2 coord = clamp(
        XRENGINE_ScreenPixelLocal(gl_FragCoord.xy, vec2(0.0), vec2(textureSize(DepthView))),
        ivec2(0),
        textureSize(DepthView) - ivec2(1));
    int sampleCount = textureSamples(DepthView);
    float resolvedDepth = texelFetch(DepthView, coord, 0).r;

    for (int sampleIndex = 1; sampleIndex < sampleCount; ++sampleIndex)
    {
        float sampleDepth = texelFetch(DepthView, coord, sampleIndex).r;
        if (IsReversedDepth)
            resolvedDepth = max(resolvedDepth, sampleDepth);
        else
            resolvedDepth = min(resolvedDepth, sampleDepth);
    }

    gl_FragDepth = resolvedDepth;
    OutColor = vec4(0.0f);
}
