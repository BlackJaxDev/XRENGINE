#version 460

layout(location = 4) in vec2 FragUV0;
layout(location = 5) flat in vec4 GlyphUVBounds;

layout(location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

uniform vec4 TextColor;
uniform vec4 OutlineColor;
uniform float OutlineThickness;

const int StrokeSampleRadius = 5;
const float StrokeFillFadeStart = 0.25;
const float StrokeFillFadeEnd = 0.85;
const float StrokeDiagonal = 0.70710678118;
const float StrokeDirA = 0.92387953251;
const float StrokeDirB = 0.38268343236;

// Sample atlas, returning 0 for UVs outside the current glyph's region
// to prevent outline bleed from neighbouring packed glyphs.
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

float SampleStroke(vec2 uv, vec2 uvDx, vec2 uvDy, float radius)
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

void main()
{
    float fill = SampleGlyph(FragUV0);

    float outlineMask = 0.0;
    if (OutlineThickness > 0.0 && OutlineColor.a > 0.0)
    {
        vec2 uvDx = dFdx(FragUV0);
        vec2 uvDy = dFdy(FragUV0);
        float stroke = SampleStroke(FragUV0, uvDx, uvDy, OutlineThickness);
        outlineMask = stroke * StrokeVisibilityMask(fill);
    }

    float fillAlpha = TextColor.a * fill;
    float outlineAlpha = OutlineColor.a * outlineMask;

    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);
    vec3 premulRgb = TextColor.rgb * fillAlpha + OutlineColor.rgb * outlineAlpha * (1.0 - fillAlpha);
    vec3 rgb = alpha > 0.0 ? premulRgb / alpha : vec3(0.0);

    FragColor = vec4(rgb, alpha);
}
