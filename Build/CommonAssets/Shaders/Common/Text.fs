#version 460

layout(location = 4) in vec2 FragUV0;
layout(location = 5) flat in vec4 GlyphUVBounds;

layout(location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

uniform vec4 TextColor;
uniform vec4 OutlineColor;
uniform float OutlineThickness;

// Sample atlas, returning 0 for UVs outside the current glyph's region
// to prevent outline bleed from neighbouring packed glyphs.
float SampleGlyph(vec2 uv)
{
    float mask = step(GlyphUVBounds.x, uv.x) * step(uv.x, GlyphUVBounds.z) *
                 step(GlyphUVBounds.y, uv.y) * step(uv.y, GlyphUVBounds.w);
    return texture(Texture0, uv).r * mask;
}

float SampleStroke(vec2 uv, vec2 texelSize, float radius)
{
    float stroke = 0.0;
    vec2 offset = texelSize * radius;
    vec2 halfOffset = offset * 0.5;

    stroke = max(stroke, SampleGlyph(uv + vec2( offset.x, 0.0)));
    stroke = max(stroke, SampleGlyph(uv + vec2(-offset.x, 0.0)));
    stroke = max(stroke, SampleGlyph(uv + vec2(0.0,  offset.y)));
    stroke = max(stroke, SampleGlyph(uv + vec2(0.0, -offset.y)));
    stroke = max(stroke, SampleGlyph(uv + vec2( offset.x,  offset.y)));
    stroke = max(stroke, SampleGlyph(uv + vec2(-offset.x,  offset.y)));
    stroke = max(stroke, SampleGlyph(uv + vec2( offset.x, -offset.y)));
    stroke = max(stroke, SampleGlyph(uv + vec2(-offset.x, -offset.y)));

    stroke = max(stroke, SampleGlyph(uv + vec2( halfOffset.x, 0.0)));
    stroke = max(stroke, SampleGlyph(uv + vec2(-halfOffset.x, 0.0)));
    stroke = max(stroke, SampleGlyph(uv + vec2(0.0,  halfOffset.y)));
    stroke = max(stroke, SampleGlyph(uv + vec2(0.0, -halfOffset.y)));

    return stroke;
}

void main()
{
    float fill = SampleGlyph(FragUV0);

    float outlineMask = 0.0;
    if (OutlineThickness > 0.0 && OutlineColor.a > 0.0)
    {
        vec2 texelSize = 1.0 / vec2(textureSize(Texture0, 0));
        float stroke = SampleStroke(FragUV0, texelSize, OutlineThickness);
        outlineMask = max(stroke - fill, 0.0);
    }

    float fillAlpha = TextColor.a * fill;
    float outlineAlpha = OutlineColor.a * outlineMask;

    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);
    vec3 premulRgb = TextColor.rgb * fillAlpha + OutlineColor.rgb * outlineAlpha * (1.0 - fillAlpha);
    vec3 rgb = alpha > 0.0 ? premulRgb / alpha : vec3(0.0);

    FragColor = vec4(rgb, alpha);
}