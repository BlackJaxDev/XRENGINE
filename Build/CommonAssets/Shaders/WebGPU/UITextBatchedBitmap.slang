// Bitmap UI text retains the shared collector's glyph and text storage layout.
#include "UIOutput.slang"
[[vk::binding(0, 1)]] StructuredBuffer<float4> glyphTransforms;
[[vk::binding(1, 1)]] StructuredBuffer<float4> glyphTexCoords;
[[vk::binding(2, 1)]] StructuredBuffer<float4> textInstances;
[[vk::binding(3, 1)]] StructuredBuffer<uint> glyphTextIndex;
[[vk::binding(0, 2)]] Texture2D<float4> fontAtlas;
[[vk::binding(1, 2)]] SamplerState fontSampler;

struct UITextInput
{
    [[vk::location(0)]] float3 position : POSITION;
    uint instanceId : SV_InstanceID;
};
struct UITextOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
    [[vk::location(1)]] nointerpolation float4 glyphUvBounds : TEXCOORD1;
    [[vk::location(2)]] nointerpolation float4 fillColor : TEXCOORD2;
    [[vk::location(3)]] nointerpolation float4 outlineColor : TEXCOORD3;
    [[vk::location(4)]] nointerpolation float outlineThickness : TEXCOORD4;
};
[shader("vertex")]
UITextOutput uiTextVertex(UITextInput input)
{
    uint textBase = glyphTextIndex[input.instanceId] * 8u;
    float4 glyph = glyphTransforms[input.instanceId];
    float4 atlasUv = glyphTexCoords[input.instanceId];
    float4 outline = textInstances[textBase + 6u];
    float thickness = textInstances[textBase + 7u].x;
    float2 corner = input.position.xy;
    float2 glyphMin = glyph.xy;
    float2 glyphSize = glyph.zw;
    float2 uvMin = atlasUv.xy;
    float2 uvMax = atlasUv.zw;
    if (thickness > 0.0 && outline.a > 0.0)
    {
        float2 expansion = float2(thickness, thickness);
        float2 uvExpansion = (uvMax - uvMin) * expansion / max(abs(glyphSize), float2(1e-6, 1e-6));
        float2 direction = float2(glyphSize.x < 0.0 ? -1.0 : 1.0, glyphSize.y < 0.0 ? -1.0 : 1.0);
        glyphMin -= expansion * direction;
        glyphSize += expansion * 2.0 * direction;
        uvMin -= uvExpansion;
        uvMax += uvExpansion;
    }
    float4 local = float4(glyphMin + corner * glyphSize, 0.0, 1.0);
    float4 world = local.x * textInstances[textBase] +
        local.y * textInstances[textBase + 1u] +
        local.z * textInstances[textBase + 2u] +
        local.w * textInstances[textBase + 3u];
    UITextOutput output;
    output.position = mul(view.viewProjection, world);
    output.uv = lerp(uvMin, uvMax, corner);
    output.glyphUvBounds = atlasUv;
    output.fillColor = textInstances[textBase + 4u];
    output.outlineColor = outline;
    output.outlineThickness = thickness;
    return output;
}
float sampleGlyph(float2 uv, float4 bounds, float2 dx, float2 dy)
{
    if (uv.x < bounds.x || uv.x > bounds.z || uv.y < bounds.y || uv.y > bounds.w)
        return 0.0;
    return fontAtlas.SampleGrad(fontSampler, uv, dx, dy).r;
}
float strokeRing(float2 uv, float4 bounds, float2 dx, float2 dy, float radius)
{
    float2 a = dx * radius;
    float2 b = dy * radius;
    float2 diagonalA = (dx + dy) * (radius * 0.70710678118);
    float2 diagonalB = (dx - dy) * (radius * 0.70710678118);
    float2 c = dx * (radius * 0.92387953251) + dy * (radius * 0.38268343236);
    float2 d = dx * (radius * 0.92387953251) - dy * (radius * 0.38268343236);
    float2 e = dx * (radius * 0.38268343236) + dy * (radius * 0.92387953251);
    float2 f = dx * (radius * 0.38268343236) - dy * (radius * 0.92387953251);
    float result = 0.0;
    result = max(result, sampleGlyph(uv + a, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - a, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + b, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - b, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + diagonalA, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - diagonalA, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + diagonalB, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - diagonalB, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + c, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - c, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + d, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - d, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + e, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - e, bounds, dx, dy));
    result = max(result, sampleGlyph(uv + f, bounds, dx, dy));
    result = max(result, sampleGlyph(uv - f, bounds, dx, dy));
    return result;
}
[shader("fragment")]
float4 uiTextFragment(UITextOutput input) : SV_Target0
{
    float2 dx = ddx(input.uv);
    float2 dy = ddy(input.uv);
    float fill = sampleGlyph(input.uv, input.glyphUvBounds, dx, dy);
    float outlineMask = 0.0;
    if (input.outlineThickness > 0.0 && input.outlineColor.a > 0.0)
    {
        float radius = clamp(input.outlineThickness, 0.0, 5.0);
        float stroke = fill;
        int steps = int(floor(radius));
        for (int i = 1; i <= 5; ++i)
        {
            if (i > steps) break;
            stroke = max(stroke, strokeRing(input.uv, input.glyphUvBounds, dx, dy, float(i)));
        }
        if (radius - float(steps) > 0.01)
            stroke = max(stroke, strokeRing(input.uv, input.glyphUvBounds, dx, dy, radius));
        outlineMask = stroke * (1.0 - smoothstep(0.25, 0.85, fill));
    }
    bool linearOutput = uiLinearOffscreenOutput();
    float3 fillColor = linearOutput ? uiDisplayToLinear(input.fillColor.rgb) : input.fillColor.rgb;
    float3 outlineColor = linearOutput ? uiDisplayToLinear(input.outlineColor.rgb) : input.outlineColor.rgb;
    float fillAlpha = (linearOutput ? saturate(input.fillColor.a) : input.fillColor.a) * fill;
    float outlineAlpha = (linearOutput ? saturate(input.outlineColor.a) : input.outlineColor.a) * outlineMask;
    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);
    float3 premulRgb = fillColor * fillAlpha +
        outlineColor * outlineAlpha * (1.0 - fillAlpha);
    if (linearOutput)
        return float4(premulRgb, alpha);
    float3 rgb = alpha > 0.0 ? premulRgb / alpha : float3(0.0, 0.0, 0.0);
    return float4(rgb, alpha);
}
