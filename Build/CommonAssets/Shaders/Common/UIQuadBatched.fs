#version 460

layout (location = 4) in vec2 FragUV0;
layout (location = 5) flat in vec4 InstanceColor;
layout (location = 6) flat in vec4 InstanceBounds;
layout (location = 7) in vec2 FragLocalPos;
layout (location = 8) flat in vec4 InstanceCornerRadii;
layout (location = 9) flat in vec4 InstanceStyleMeta;
layout (location = 10) flat in vec4 InstanceBorderColor;
layout (location = 11) flat in vec4 InstanceGradientBottom;
layout (location = 12) flat in vec4 InstanceShadowColor;
layout (location = 13) flat in vec4 InstanceShadowParams;
layout (location = 14) flat in vec4 InstanceBlurParams;

layout (binding = 5) uniform sampler2D Texture0;
uniform int UiBackdropBlurAvailable;
uniform vec2 UiViewportSize;

layout (location = 0) out vec4 OutColor;

const uint FlagStyledBox = 1u;
const uint FlagDecodeSrgbColors = 2u;
const uint FlagVerticalGradient = 4u;
const uint FlagDropShadow = 8u;
const uint FlagBackdropBlur = 16u;

vec3 srgbToLinear(vec3 srgb)
{
    vec3 clamped = max(srgb, vec3(0.0));
    vec3 low = clamped / 12.92;
    vec3 high = pow((clamped + 0.055) / 1.055, vec3(2.4));
    return mix(low, high, step(vec3(0.04045), clamped));
}

float sdRoundedBox(vec2 p, vec2 halfSize, float radius, float cornerExp)
{
    vec2 q = abs(p) - halfSize + vec2(radius);
    vec2 outside = max(q, vec2(0.0));
    float outsideDist;
    if (cornerExp > 2.01 && radius > 0.001)
    {
        vec2 n = outside / radius;
        outsideDist = pow(pow(n.x, cornerExp) + pow(n.y, cornerExp), 1.0 / cornerExp) * radius;
    }
    else
    {
        outsideDist = length(outside);
    }
    return outsideDist + min(max(q.x, q.y), 0.0) - radius;
}

void main()
{
    uint flags = uint(InstanceStyleMeta.w + 0.5);
    if (flags == 0u)
    {
        OutColor = InstanceColor;
        return;
    }

    bool decodeSrgb = (flags & FlagDecodeSrgbColors) != 0u;
    vec4 topColor = InstanceColor;
    vec4 gradBottom = InstanceGradientBottom;
    vec4 borderColor = InstanceBorderColor;
    vec4 shadowColor = InstanceShadowColor;

    if (decodeSrgb)
    {
        topColor.rgb = srgbToLinear(topColor.rgb);
        gradBottom.rgb = srgbToLinear(gradBottom.rgb);
        borderColor.rgb = srgbToLinear(borderColor.rgb);
        shadowColor.rgb = srgbToLinear(shadowColor.rgb);
    }

    if ((flags & (FlagStyledBox | FlagDropShadow | FlagBackdropBlur | FlagVerticalGradient)) == 0u)
    {
        OutColor = topColor;
        return;
    }

    vec2 boxSize = max(InstanceBounds.zw, vec2(1.0));
    vec2 halfSize = boxSize * 0.5;
    vec2 p = FragLocalPos - halfSize;

    // Select per-corner radius (UI +Y is top, -Y is bottom).
    float radius = (p.y > 0.0)
        ? ((p.x > 0.0) ? InstanceCornerRadii.y : InstanceCornerRadii.x)
        : ((p.x > 0.0) ? InstanceCornerRadii.z : InstanceCornerRadii.w);
    radius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));

    float cornerExp = max(InstanceStyleMeta.x, 2.0);
    float sdf = sdRoundedBox(p, halfSize, radius, cornerExp);
    float aa = max(fwidth(sdf), 0.75);
    float fillMask = 1.0 - smoothstep(-aa * 0.5, aa * 0.5, sdf);

    // UI +Y is top (vTop = 1 at top edge, 0 at bottom edge).
    float vTop = clamp(FragLocalPos.y / boxSize.y, 0.0, 1.0);
    vec4 fillColor = ((flags & FlagVerticalGradient) != 0u)
        ? mix(gradBottom, topColor, vTop)
        : topColor;

    vec3 boxRgb = fillColor.rgb;
    float boxAlpha = fillColor.a * fillMask;

    // Sample the 3-mip Dual-Kawase backdrop blur pyramid via hardware trilinear LOD so each quad
    // can choose its own independent blur radius continuously in a single texture bind.
    if ((flags & FlagBackdropBlur) != 0u && UiBackdropBlurAvailable != 0 && InstanceBlurParams.y > 0.001 && fillMask > 0.0)
    {
        vec2 screenUV = clamp(gl_FragCoord.xy / max(UiViewportSize, vec2(1.0)), vec2(0.0), vec2(1.0));
        float radiusNorm = clamp(InstanceBlurParams.x / 28.0, 0.0, 1.0);
        float blurLod = radiusNorm * 1.65;
        vec3 backdrop = textureLod(Texture0, screenUV, blurLod).rgb;
        float luma = dot(backdrop, vec3(0.2126, 0.7152, 0.0722));
        float sat = InstanceBlurParams.z > 0.0 ? InstanceBlurParams.z : 1.25;
        float bright = InstanceBlurParams.w > 0.0 ? InstanceBlurParams.w : 1.0;
        vec3 vibrant = max(mix(vec3(luma), backdrop, sat) * bright, vec3(0.0));
        vec3 frostedRgb = mix(vibrant, fillColor.rgb, fillColor.a);
        float blurBlend = clamp(InstanceBlurParams.y, 0.0, 1.0);
        boxRgb = mix(fillColor.rgb, frostedRgb, blurBlend);
        boxAlpha = mix(fillColor.a, 1.0, blurBlend) * fillMask;
    }

    // Inner hairline border with subtle top-edge specular sheen.
    float borderWidth = InstanceStyleMeta.y;
    float borderHighlight = InstanceStyleMeta.z;
    if (borderWidth > 0.0 && borderColor.a > 0.0 && fillMask > 0.0)
    {
        float innerMask = 1.0 - smoothstep(-borderWidth - aa * 0.5, -borderWidth + aa * 0.5, sdf);
        float borderMask = clamp(fillMask - innerMask, 0.0, 1.0);
        float topSheen = pow(vTop, 2.5) * borderHighlight;
        vec3 rimRgb = borderColor.rgb + vec3(topSheen);
        float rimAlpha = clamp(borderColor.a + topSheen * 0.4, 0.0, 1.0) * borderMask;
        boxRgb = mix(boxRgb, rimRgb, rimAlpha);
        boxAlpha = max(boxAlpha, rimAlpha);
    }

    // Analytic soft rounded-box drop shadow outside the box silhouette.
    vec4 shadowOut = vec4(0.0);
    if ((flags & FlagDropShadow) != 0u && shadowColor.a > 0.0 && (InstanceShadowParams.z > 0.0 || InstanceShadowParams.w != 0.0))
    {
        vec2 shadowOffset = InstanceShadowParams.xy;
        float shadowBlur = InstanceShadowParams.z;
        float shadowSpread = InstanceShadowParams.w;

        vec2 shadowP = p - vec2(shadowOffset.x, -shadowOffset.y);
        vec2 shadowHalf = max(halfSize + vec2(shadowSpread), vec2(0.5));
        float shadowRadius = clamp(radius + shadowSpread, 0.0, min(shadowHalf.x, shadowHalf.y));
        float shadowSdf = sdRoundedBox(shadowP, shadowHalf, shadowRadius, cornerExp);
        float sigma = max(shadowBlur * 0.5, 0.5);
        float t = 1.0 - smoothstep(-sigma * 0.75, sigma * 1.9, shadowSdf);
        float shadowFactor = t * t * (3.0 - 2.0 * t);
        float outsideMask = 1.0 - fillMask;
        shadowOut = vec4(shadowColor.rgb, shadowColor.a * shadowFactor * outsideMask);
    }

    // Straight-alpha composite: box over drop shadow.
    float outAlpha = boxAlpha + shadowOut.a * (1.0 - boxAlpha);
    if (outAlpha <= 1e-5)
        discard;

    vec3 outRgb = (boxRgb * boxAlpha + shadowOut.rgb * shadowOut.a * (1.0 - boxAlpha)) / outAlpha;
    OutColor = vec4(outRgb, outAlpha);
}
