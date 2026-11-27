#version 450 core

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

layout(binding = 0) uniform sampler2DMS ColorView;

// Returns the standard hardware MSAA subpixel offset relative to the pixel center.
vec2 StandardMsaaSampleOffset(int sampleCount, int sampleIndex)
{
    if (sampleCount == 2)
        return sampleIndex == 0 ? vec2(0.25f, 0.25f) : vec2(-0.25f, -0.25f);
    if (sampleCount == 4)
    {
        const vec2 offsets4[4] = vec2[4](
            vec2(-0.125f, -0.375f),
            vec2( 0.375f, -0.125f),
            vec2(-0.375f,  0.125f),
            vec2( 0.125f,  0.375f));
        return offsets4[clamp(sampleIndex, 0, 3)];
    }
    if (sampleCount == 8)
    {
        const vec2 offsets8[8] = vec2[8](
            vec2( 0.0625f, -0.1875f),
            vec2(-0.0625f,  0.1875f),
            vec2( 0.3125f,  0.0625f),
            vec2(-0.1875f, -0.3125f),
            vec2(-0.3125f,  0.3125f),
            vec2(-0.4375f, -0.0625f),
            vec2( 0.1875f,  0.4375f),
            vec2( 0.4375f, -0.4375f));
        return offsets8[clamp(sampleIndex, 0, 7)];
    }
    return vec2(0.0f);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    ivec2 extent = textureSize(ColorView);
    ivec2 coord = clamp(
        XRENGINE_ScreenPixelLocal(gl_FragCoord.xy, vec2(0.0f), vec2(extent)),
        ivec2(0),
        extent - ivec2(1));
    int sampleCount = max(textureSamples(ColorView), 1);

    vec4 colorSum = vec4(0.0f);
    float weightSum = 0.0f;

    for (int oy = -1; oy <= 1; ++oy)
    {
        for (int ox = -1; ox <= 1; ++ox)
        {
            ivec2 sampleCoord = coord + ivec2(ox, oy);
            if (any(lessThan(sampleCoord, ivec2(0))) || any(greaterThanEqual(sampleCoord, extent)))
                continue;

            for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex)
            {
                vec2 delta = vec2(float(ox), float(oy)) + StandardMsaaSampleOffset(sampleCount, sampleIndex);
                float wx = 1.0f - abs(delta.x);
                float wy = 1.0f - abs(delta.y);
                if (wx <= 0.0f || wy <= 0.0f)
                    continue;

                float weight = wx * wy;
                colorSum += texelFetch(ColorView, sampleCoord, sampleIndex) * weight;
                weightSum += weight;
            }
        }
    }

    OutColor = colorSum / max(weightSum, 1e-6f);
}
