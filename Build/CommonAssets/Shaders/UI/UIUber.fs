#version 460

layout (location = 0) out vec4 OutColor;

layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;

struct UIUberInstanceStyle
{
    vec4 baseColor;
    vec4 gradientColor;      // rgb = secondary color, a = gradient strength
    vec4 gradientRange;      // start.xy, end.xy in 0-1 uv space
    vec4 innerStroke;        // rgb color, a = thickness (px)
    vec4 middleStroke;       // rgb color, a = thickness (px)
    vec4 outerStroke;        // rgb color, a = thickness (px)
    vec4 innerGlowColor;     // rgb color, a = intensity
    vec4 outerGlowColor;     // rgb color, a = intensity
    vec4 glowParams;         // x = inner glow radius (px), y = outer glow radius (px), zw reserved
    vec4 innerShadowColor;   // rgb color, a = intensity
    vec4 outerShadowColor;   // rgb color, a = intensity
    vec4 shadowAnglesDist;   // x = inner angle (radians), y = inner distance (px), z = outer angle (radians), w = outer distance (px)
    vec4 shadowRadii;        // x = inner shadow radius (px), y = outer shadow radius (px), zw reserved
    vec4 fillParams;         // x = corner radius (px), y = texture opacity, z = use texture, w = alpha cutoff
    vec4 gradientFeather;    // x = gradient feather (0-1), y = unused, zw reserved
};

layout(std430, binding = 0) readonly buffer UIUberInstanceStyleBuffer
{
    UIUberInstanceStyle InstanceStyles[];
};

uniform vec4 BaseColor = vec4(1.0);
uniform vec4 GradientColor = vec4(1.0, 1.0, 1.0, 0.0);
uniform vec4 GradientRange = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec4 InnerStroke = vec4(0.0);
uniform vec4 MiddleStroke = vec4(0.0);
uniform vec4 OuterStroke = vec4(0.0);
uniform vec4 InnerGlowColor = vec4(0.0);
uniform vec4 OuterGlowColor = vec4(0.0);
uniform vec4 GlowParams = vec4(0.0);
uniform vec4 InnerShadowColor = vec4(0.0);
uniform vec4 OuterShadowColor = vec4(0.0);
uniform vec4 ShadowAnglesDist = vec4(0.0);
uniform vec4 ShadowRadii = vec4(0.0);
uniform vec4 GradientFeather = vec4(0.0);

uniform float CornerRadius = 0.0;     // In pixels
uniform float TextureOpacity = 1.0;
uniform float UseTexture = 0.0;
uniform float AlphaCutoff = 0.0;

uniform float UIWidth = 1.0;
uniform float UIHeight = 1.0;
uniform vec4 UIXYWH = vec4(0.0);

uniform float UseInstanceData = 0.0;  // When > 0, pull styles from the SSBO
uniform int InstanceDataOffset = 0;
uniform float PrimitivesPerInstance = 2.0; // Triangles per quad, used to derive instance index from primitive id

const float MIN_AA = 1e-4;

float RoundedDistance(vec2 posPx, vec2 sizePx, float radiusPx)
{
    vec2 halfSize = sizePx * 0.5;
    float radius = clamp(radiusPx, 0.0, min(halfSize.x, halfSize.y));
    vec2 q = abs(posPx - halfSize) - (halfSize - vec2(radius));
    float outside = length(max(q, 0.0)) - radius;
    float inside = min(max(q.x, q.y), 0.0) - radius;
    return outside + inside;
}

float MaskFill(float dist, float aa)
{
    return clamp(1.0 - smoothstep(0.0, aa, dist), 0.0, 1.0);
}

float MaskInnerStroke(float dist, float thickness, float aa)
{
    if (thickness <= 0.0)
        return 0.0;

    float d = -dist; // inside distance, positive when inside
    float start = smoothstep(0.0, aa, d);
    float end = smoothstep(thickness, thickness + aa, d);
    return clamp(start - end, 0.0, 1.0);
}

float MaskMiddleStroke(float dist, float thickness, float aa)
{
    if (thickness <= 0.0)
        return 0.0;

    float halfT = thickness * 0.5;
    float centerFade = 1.0 - smoothstep(0.0, aa, abs(dist));
    float falloff = smoothstep(halfT, halfT + aa, abs(dist));
    return clamp(centerFade * (1.0 - falloff), 0.0, 1.0);
}

float MaskOuterStroke(float dist, float thickness, float aa)
{
    if (thickness <= 0.0)
        return 0.0;

    float start = smoothstep(0.0, aa, dist);
    float end = smoothstep(thickness, thickness + aa, dist);
    return clamp(start - end, 0.0, 1.0);
}

float MaskOuterGlow(float dist, float radius, float intensity, float aa)
{
    if (radius <= 0.0 || intensity <= 0.0)
        return 0.0;

    float glow = 1.0 - smoothstep(radius, radius + aa * 4.0, max(dist, 0.0));
    return clamp(glow * intensity, 0.0, 1.0);
}

float MaskInnerGlow(float dist, float radius, float intensity, float aa)
{
    if (radius <= 0.0 || intensity <= 0.0)
        return 0.0;

    float inside = max(-dist, 0.0);
    float glow = 1.0 - smoothstep(radius, radius + aa * 4.0, inside);
    return clamp(glow * intensity, 0.0, 1.0);
}

float MaskShadow(vec2 posPx, vec2 sizePx, float radiusPx, vec2 offsetPx, float shadowRadius, float intensity, bool inner, float aa)
{
    if (shadowRadius <= 0.0 || intensity <= 0.0)
        return 0.0;

    float dist = RoundedDistance(posPx - offsetPx, sizePx, radiusPx);
    float signedDist = inner ? -dist : dist;
    float shadow = 1.0 - smoothstep(0.0, shadowRadius + aa, signedDist);
    return clamp(shadow * intensity, 0.0, 1.0);
}

float ComputeGradient(vec2 uv, vec4 range, float feather)
{
    vec2 start = range.xy;
    vec2 end = range.zw;
    vec2 dir = end - start;
    float lenSq = max(dot(dir, dir), MIN_AA);
    float t = clamp(dot(uv - start, dir) / lenSq, 0.0, 1.0);
    if (feather > 0.0)
    {
        float f = clamp(feather, 0.0, 1.0);
        t = smoothstep(0.0, 1.0 - f, t);
    }
    return t;
}

vec2 PolarOffset(float angleRadians, float distance)
{
    return vec2(cos(angleRadians), sin(angleRadians)) * distance;
}

UIUberInstanceStyle BuildStyleFromUniforms()
{
    UIUberInstanceStyle style;
    style.baseColor = BaseColor;
    style.gradientColor = GradientColor;
    style.gradientRange = GradientRange;
    style.innerStroke = InnerStroke;
    style.middleStroke = MiddleStroke;
    style.outerStroke = OuterStroke;
    style.innerGlowColor = InnerGlowColor;
    style.outerGlowColor = OuterGlowColor;
    style.glowParams = GlowParams;
    style.innerShadowColor = InnerShadowColor;
    style.outerShadowColor = OuterShadowColor;
    style.shadowAnglesDist = ShadowAnglesDist;
    style.shadowRadii = ShadowRadii;
    style.fillParams = vec4(CornerRadius, TextureOpacity, UseTexture, AlphaCutoff);
    style.gradientFeather = GradientFeather;
    return style;
}

UIUberInstanceStyle ResolveStyle()
{
    if (UseInstanceData > 0.5)
    {
        int primitiveIndex = gl_PrimitiveID;
        int instanceIndex = InstanceDataOffset + int(float(primitiveIndex) / max(PrimitivesPerInstance, 1.0));
        return InstanceStyles[instanceIndex];
    }

    return BuildStyleFromUniforms();
}

void main()
{
    vec2 sizePx = vec2(UIWidth, UIHeight);
    vec2 posPx = FragUV0 * sizePx;

    UIUberInstanceStyle style = ResolveStyle();

    float dist = RoundedDistance(posPx, sizePx, style.fillParams.x);
    float aa = max(fwidth(dist), MIN_AA);

    float fillMask = MaskFill(dist, aa);
    float innerStrokeMask = MaskInnerStroke(dist, style.innerStroke.a, aa);
    float middleStrokeMask = MaskMiddleStroke(dist, style.middleStroke.a, aa);
    float outerStrokeMask = MaskOuterStroke(dist, style.outerStroke.a, aa);
    float innerGlowMask = MaskInnerGlow(dist, style.glowParams.x, style.innerGlowColor.a, aa);
    float outerGlowMask = MaskOuterGlow(dist, style.glowParams.y, style.outerGlowColor.a, aa);

    vec2 innerShadowOffset = PolarOffset(style.shadowAnglesDist.x, style.shadowAnglesDist.y);
    vec2 outerShadowOffset = PolarOffset(style.shadowAnglesDist.z, style.shadowAnglesDist.w);

    float innerShadowMask = MaskShadow(posPx, sizePx, style.fillParams.x, innerShadowOffset, style.shadowRadii.x, style.innerShadowColor.a, true, aa);
    float outerShadowMask = MaskShadow(posPx, sizePx, style.fillParams.x, outerShadowOffset, style.shadowRadii.y, style.outerShadowColor.a, false, aa);

    vec4 sampled = texture(Texture0, FragUV0) * style.baseColor;
    vec4 content = mix(style.baseColor, sampled, style.fillParams.z);
    content.a *= style.fillParams.y;

    float gradientT = ComputeGradient(FragUV0, style.gradientRange, style.gradientFeather.x);
    vec4 gradientColor = mix(content, style.gradientColor, gradientT * style.gradientColor.a);

    vec4 fill = gradientColor * fillMask;
    vec4 innerStroke = vec4(style.innerStroke.rgb, 1.0) * innerStrokeMask;
    vec4 middleStroke = vec4(style.middleStroke.rgb, 1.0) * middleStrokeMask;
    vec4 outerStroke = vec4(style.outerStroke.rgb, 1.0) * outerStrokeMask;
    vec4 innerGlow = vec4(style.innerGlowColor.rgb, 1.0) * innerGlowMask;
    vec4 outerGlow = vec4(style.outerGlowColor.rgb, 1.0) * outerGlowMask;
    vec4 innerShadow = vec4(style.innerShadowColor.rgb, 1.0) * innerShadowMask;
    vec4 outerShadow = vec4(style.outerShadowColor.rgb, 1.0) * outerShadowMask;

    vec4 combined = outerShadow + outerGlow + innerShadow + innerGlow + outerStroke + middleStroke + innerStroke + fill;
    combined = clamp(combined, 0.0, 1.0);

    if (style.fillParams.w > 0.0 && combined.a < style.fillParams.w)
        discard;

    OutColor = combined;
}
