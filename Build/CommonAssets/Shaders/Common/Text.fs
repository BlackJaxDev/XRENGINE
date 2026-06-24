#version 460

layout(location = 4) in vec2 FragUV0;
layout(location = 5) flat in vec4 GlyphUVBounds;

layout(location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

uniform vec4 TextColor;
uniform vec4 OutlineColor;
uniform float OutlineThickness;

const int StrokeSampleRadius = 5;
const float FillCoverageGuard = 0.02;

// Sample atlas, returning 0 for UVs outside the current glyph's region
// to prevent outline bleed from neighbouring packed glyphs.
float SampleGlyph(vec2 uv)
{
    float mask = step(GlyphUVBounds.x, uv.x) * step(uv.x, GlyphUVBounds.z) *
                 step(GlyphUVBounds.y, uv.y) * step(uv.y, GlyphUVBounds.w);
    return texture(Texture0, uv).r * mask;
}

float SampleStroke(vec2 uv, vec2 uvDx, vec2 uvDy, float radius)
{
    float stroke = 0.0;
    float clampedRadius = clamp(radius, 0.0, float(StrokeSampleRadius));
    float radiusSquared = clampedRadius * clampedRadius;

    for (int y = -StrokeSampleRadius; y <= StrokeSampleRadius; y++)
    {
        for (int x = -StrokeSampleRadius; x <= StrokeSampleRadius; x++)
        {
            vec2 sampleOffset = vec2(float(x), float(y));
            float distanceSquared = dot(sampleOffset, sampleOffset);
            if (distanceSquared <= radiusSquared)
                stroke = max(stroke, SampleGlyph(uv + uvDx * sampleOffset.x + uvDy * sampleOffset.y));
        }
    }

    return stroke;
}

float OutsideGlyphMask(float fill)
{
    return 1.0 - smoothstep(0.0, FillCoverageGuard, fill);
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
        outlineMask = stroke * OutsideGlyphMask(fill);
    }

    float fillAlpha = TextColor.a * fill;
    float outlineAlpha = OutlineColor.a * outlineMask;

    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);
    vec3 premulRgb = TextColor.rgb * fillAlpha + OutlineColor.rgb * outlineAlpha * (1.0 - fillAlpha);
    vec3 rgb = alpha > 0.0 ? premulRgb / alpha : vec3(0.0);

    FragColor = vec4(rgb, alpha);
}
