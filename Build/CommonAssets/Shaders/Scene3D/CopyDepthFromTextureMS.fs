#version 450 core

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

layout(binding = 0) uniform sampler2DMS DepthView;
uniform bool IsReversedDepth;

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0f || clipXY.y > 1.0f)
        discard;

    ivec2 coord = ivec2(gl_FragCoord.xy);
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