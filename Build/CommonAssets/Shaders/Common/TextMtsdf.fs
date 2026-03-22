#version 460

layout(location = 4) in vec2 FragUV0;

layout(location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

uniform vec4 TextColor;
uniform vec4 OutlineColor;
uniform float OutlineThickness;
uniform float MsdfDistanceRange;
uniform float MsdfDistanceRangeMiddle;
uniform float MsdfFillBias;

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

void main()
{
    vec4 sampleValue = texture(Texture0, FragUV0);

    // Multi-channel true SDF reconstruction (Chlumsky's dual-clamp correction)
    float sd = Median(sampleValue.rgb);
    sd = max(min(sd, sampleValue.a), max(sd, sampleValue.a) - 0.5);

    float pxRange = ScreenPxRange();

    float signedFillDistance = pxRange * (sd - MsdfDistanceRangeMiddle);
    float signedOutlineDistance = pxRange * (sampleValue.a - MsdfDistanceRangeMiddle);

    float fill = clamp(signedFillDistance + MsdfFillBias, 0.0, 1.0);
    float outer = clamp(signedOutlineDistance + OutlineThickness + 0.5, 0.0, 1.0);
    float outline = max(outer - fill, 0.0);

    float fillAlpha = TextColor.a * fill;
    float outlineAlpha = OutlineColor.a * outline;
    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);

    if (alpha < 0.01) discard;

    // Composite fill over outline, then un-premultiply for SrcAlpha blending
    vec3 premulRgb = (TextColor.rgb * fillAlpha) + (OutlineColor.rgb * outlineAlpha * (1.0 - fillAlpha));
    vec3 rgb = premulRgb / alpha;

    FragColor = vec4(rgb, alpha);
}