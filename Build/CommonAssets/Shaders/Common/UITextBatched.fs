#version 460

layout (location = 4) in vec2 FragUV0;
layout (location = 5) flat in vec4 InstanceTextColor;

layout (location = 0) out vec4 FragColor;

uniform sampler2D Texture0;
uniform int TextAtlasType;
uniform float MsdfDistanceRange;
uniform float MsdfDistanceRangeMiddle;
uniform float MsdfFillBias;
uniform int TextDebugMode;

const int TextAtlasBitmap = 0;
const int TextAtlasMsdf = 1;
const int TextAtlasMtsdf = 2;

float Median(vec3 value)
{
    return max(min(value.r, value.g), min(max(value.r, value.g), value.b));
}

float ScreenPxRange()
{
    vec2 unitRange = vec2(MsdfDistanceRange) / vec2(textureSize(Texture0, 0));
    vec2 screenTexSize = vec2(1.0) / max(fwidth(FragUV0), vec2(1e-6));
    return max(0.5 * dot(unitRange, screenTexSize), 1.0);
}

float ResolveCoverage(vec4 sampleValue)
{
    if (TextAtlasType == TextAtlasBitmap)
        return sampleValue.r;

    float sd = Median(sampleValue.rgb);

    if (TextAtlasType == TextAtlasMtsdf)
    {
        sd = max(min(sd, sampleValue.a), max(sd, sampleValue.a) - 0.5);
        float signedFillDistance = ScreenPxRange() * (sd - MsdfDistanceRangeMiddle);
        return clamp(signedFillDistance + MsdfFillBias, 0.0, 1.0);
    }

    float signedDistance = (sd - MsdfDistanceRangeMiddle) * MsdfDistanceRange;
    float smoothing = max(fwidth(signedDistance), 1e-6);
    return smoothstep(-smoothing, smoothing, signedDistance);
}

void main()
{
    if (TextDebugMode == 1)
    {
        FragColor = vec4(1.0, 0.0, 1.0, 1.0);
        return;
    }

    if (TextDebugMode == 2)
    {
        FragColor = vec4(fract(FragUV0 * 64.0), 0.0, 1.0);
        return;
    }

    if (TextDebugMode == 3)
    {
        FragColor = vec4(0.0, 1.0, 1.0, 1.0);
        return;
    }

    float coverage = ResolveCoverage(texture(Texture0, FragUV0));
    float alpha = InstanceTextColor.a * coverage;
    FragColor = vec4(InstanceTextColor.rgb, alpha);
}
