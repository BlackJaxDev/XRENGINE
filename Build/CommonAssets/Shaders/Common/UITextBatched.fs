#version 460

layout (location = 4) in vec2 FragUV0;
layout (location = 5) flat in vec4 InstanceTextColor;
layout (location = 6) flat in vec4 GlyphUVBounds;
layout (location = 7) flat in vec4 InstanceOutlineColor;
layout (location = 8) flat in vec4 InstanceOutlineParams;

layout (location = 0) out vec4 FragColor;

layout(binding = 4) uniform sampler2D Texture0;
uniform int TextAtlasType;
uniform float MsdfDistanceRange;
uniform float MsdfDistanceRangeMiddle;
uniform float MsdfFillBias;
uniform int TextDebugMode;
uniform int TextRenderLayer;

const int TextAtlasBitmap = 0;
const int TextAtlasMsdf = 1;
const int TextAtlasMtsdf = 2;
const int StrokeSampleRadius = 5;
const int TextRenderLayerCombined = 0;
const int TextRenderLayerOutline = 1;
const int TextRenderLayerFill = 2;
const float StrokeFillFadeStart = 0.25;
const float StrokeFillFadeEnd = 0.85;
const float StrokeDiagonal = 0.70710678118;
const float StrokeDirA = 0.92387953251;
const float StrokeDirB = 0.38268343236;

float Median(vec3 value)
{
    return max(min(value.r, value.g), min(max(value.r, value.g), value.b));
}

float SampleGlyph(vec2 uv)
{
    float mask = step(GlyphUVBounds.x, uv.x) * step(uv.x, GlyphUVBounds.z) *
                 step(GlyphUVBounds.y, uv.y) * step(uv.y, GlyphUVBounds.w);
    return texture(Texture0, uv).r * mask;
}

float SampleStrokeAt(vec2 uv, vec2 uvDx, vec2 uvDy, vec2 sampleOffset)
{
    return SampleGlyph(uv + uvDx * sampleOffset.x + uvDy * sampleOffset.y);
}

float SampleStrokeRing(vec2 uv, vec2 uvDx, vec2 uvDy, float ringRadius)
{
    float stroke = 0.0;

    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( ringRadius, 0.0)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-ringRadius, 0.0)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(0.0,  ringRadius)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(0.0, -ringRadius)));

    float diagonal = ringRadius * StrokeDiagonal;
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( diagonal,  diagonal)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-diagonal,  diagonal)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( diagonal, -diagonal)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-diagonal, -diagonal)));

    float dirA = ringRadius * StrokeDirA;
    float dirB = ringRadius * StrokeDirB;
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( dirA,  dirB)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-dirA,  dirB)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( dirA, -dirB)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-dirA, -dirB)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( dirB,  dirA)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-dirB,  dirA)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2( dirB, -dirA)));
    stroke = max(stroke, SampleStrokeAt(uv, uvDx, uvDy, vec2(-dirB, -dirA)));

    return stroke;
}

float SampleBitmapStroke(vec2 uv, vec2 uvDx, vec2 uvDy, float radius)
{
    float clampedRadius = clamp(radius, 0.0, float(StrokeSampleRadius));
    float stroke = SampleGlyph(uv);
    int wholeSteps = int(floor(clampedRadius));

    for (int stepIndex = 1; stepIndex <= StrokeSampleRadius; stepIndex++)
    {
        if (stepIndex > wholeSteps)
            break;

        stroke = max(stroke, SampleStrokeRing(uv, uvDx, uvDy, float(stepIndex)));
    }

    if (clampedRadius - float(wholeSteps) > 0.01)
        stroke = max(stroke, SampleStrokeRing(uv, uvDx, uvDy, clampedRadius));

    return stroke;
}

float StrokeVisibilityMask(float fill)
{
    return 1.0 - smoothstep(StrokeFillFadeStart, StrokeFillFadeEnd, fill);
}

float ScreenPxRange()
{
    vec2 unitRange = vec2(MsdfDistanceRange) / vec2(textureSize(Texture0, 0));
    vec2 screenTexSize = vec2(1.0) / max(fwidth(FragUV0), vec2(1e-6));
    return max(0.5 * dot(unitRange, screenTexSize), 1.0);
}

float ResolveCoverage(vec2 uv)
{
    if (TextAtlasType == TextAtlasBitmap)
        return SampleGlyph(uv);

    vec4 sampleValue = texture(Texture0, uv);
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

    if (TextDebugMode == 4)
    {
        float coverage = ResolveCoverage(FragUV0);
        FragColor = vec4(coverage, coverage, coverage, 1.0);
        return;
    }

    if (TextDebugMode == 5)
    {
        FragColor = vec4(InstanceTextColor.rgb, 1.0);
        return;
    }

    if (TextDebugMode == 6)
    {
        FragColor = vec4(vec3(InstanceTextColor.a), 1.0);
        return;
    }

    float fill = ResolveCoverage(FragUV0);
    float outlineMask = 0.0;
    float outlineThickness = InstanceOutlineParams.x;
    if (TextAtlasType == TextAtlasBitmap && outlineThickness > 0.0 && InstanceOutlineColor.a > 0.0)
    {
        vec2 uvDx = dFdx(FragUV0);
        vec2 uvDy = dFdy(FragUV0);
        float stroke = SampleBitmapStroke(FragUV0, uvDx, uvDy, outlineThickness);
        outlineMask = stroke * StrokeVisibilityMask(fill);
    }

    float fillAlpha = InstanceTextColor.a * fill;
    float outlineAlpha = InstanceOutlineColor.a * outlineMask;
    if (TextRenderLayer == TextRenderLayerOutline)
    {
        FragColor = vec4(InstanceOutlineColor.rgb, outlineAlpha);
        return;
    }

    if (TextRenderLayer == TextRenderLayerFill)
    {
        FragColor = vec4(InstanceTextColor.rgb, fillAlpha);
        return;
    }

    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);
    vec3 premulRgb = InstanceTextColor.rgb * fillAlpha + InstanceOutlineColor.rgb * outlineAlpha * (1.0 - fillAlpha);
    vec3 rgb = alpha > 0.0 ? premulRgb / alpha : vec3(0.0);

    FragColor = vec4(rgb, alpha);
}
