#version 460

layout(location = 4) in vec2 FragUV0;

layout(location = 0) out vec4 FragColor;

uniform sampler2D Texture0;

uniform vec4 TextColor;
uniform vec4 OutlineColor;
uniform float OutlineThickness;
uniform float MsdfDistanceRange;
uniform float MsdfDistanceRangeMiddle;

float Median(vec3 value)
{
    return max(min(value.r, value.g), min(max(value.r, value.g), value.b));
}

void main()
{
    vec3 sampleRgb = texture(Texture0, FragUV0).rgb;
    float distanceValue = Median(sampleRgb);
    float signedDistance = (distanceValue - MsdfDistanceRangeMiddle) * MsdfDistanceRange;
    float smoothing = max(fwidth(signedDistance), 1e-6);

    float fill = smoothstep(-smoothing, smoothing, signedDistance);
    float outer = smoothstep(-smoothing, smoothing, signedDistance + OutlineThickness);
    float outline = max(outer - fill, 0.0);

    float fillAlpha = TextColor.a * fill;
    float outlineAlpha = OutlineColor.a * outline;
    float alpha = fillAlpha + outlineAlpha * (1.0 - fillAlpha);
    vec3 premulRgb = (TextColor.rgb * fillAlpha) + (OutlineColor.rgb * outlineAlpha * (1.0 - fillAlpha));
    vec3 rgb = alpha > 0.0 ? premulRgb / alpha : vec3(0.0);

    FragColor = vec4(rgb, alpha);
}
