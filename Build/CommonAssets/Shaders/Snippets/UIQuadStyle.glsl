// Box styling for batched UI quads: rounded or squircle corners, vertical gradient,
// inner hairline border with a top-edge sheen, drop shadow, and optional frosted backdrop.
// The including shader declares InstanceColor, InstanceBounds, FragLocalPos, and the
// InstanceCornerRadii..InstanceBlurParams inputs written by UIQuadBatched.vs.

const uint XRENGINE_UiFlagStyledBox = 1u;
const uint XRENGINE_UiFlagDecodeSrgbColors = 2u;
const uint XRENGINE_UiFlagVerticalGradient = 4u;
const uint XRENGINE_UiFlagDropShadow = 8u;
const uint XRENGINE_UiFlagBackdropBlur = 16u;

uint XRENGINE_UiStyleFlags()
{
    return uint(InstanceStyleMeta.w + 0.5);
}

vec3 XRENGINE_UiSrgbToLinear(vec3 srgb)
{
    vec3 clamped = max(srgb, vec3(0.0));
    vec3 low = clamped / 12.92;
    vec3 high = pow((clamped + 0.055) / 1.055, vec3(2.4));
    return mix(low, high, step(vec3(0.04045), clamped));
}

// Signed distance to a rounded box. An exponent above 2 gives superellipse (squircle) corners.
float XRENGINE_UiRoundedBoxSdf(vec2 p, vec2 halfSize, float radius, float cornerExp)
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

// Shades one fragment of a styled quad. useBackdrop selects the frosted path, and backdrop
// holds the blurred scene color behind the fragment. Returns straight-alpha color.
// discardFragment is true when the fragment is fully transparent.
vec4 XRENGINE_UiShadeStyledQuad(bool useBackdrop, vec3 backdrop, out bool discardFragment)
{
    discardFragment = false;
    uint flags = XRENGINE_UiStyleFlags();
    if (flags == 0u)
        return InstanceColor;

    bool decodeSrgb = (flags & XRENGINE_UiFlagDecodeSrgbColors) != 0u;
    vec4 topColor = InstanceColor;
    vec4 gradBottom = InstanceGradientBottom;
    vec4 borderColor = InstanceBorderColor;
    vec4 shadowColor = InstanceShadowColor;
    if (decodeSrgb)
    {
        topColor.rgb = XRENGINE_UiSrgbToLinear(topColor.rgb);
        gradBottom.rgb = XRENGINE_UiSrgbToLinear(gradBottom.rgb);
        borderColor.rgb = XRENGINE_UiSrgbToLinear(borderColor.rgb);
        shadowColor.rgb = XRENGINE_UiSrgbToLinear(shadowColor.rgb);
    }

    if ((flags & (XRENGINE_UiFlagStyledBox | XRENGINE_UiFlagDropShadow |
        XRENGINE_UiFlagBackdropBlur | XRENGINE_UiFlagVerticalGradient)) == 0u)
        return topColor;

    vec2 boxSize = max(InstanceBounds.zw, vec2(1.0));
    vec2 halfSize = boxSize * 0.5;
    vec2 p = FragLocalPos - halfSize;

    // UI +Y is up, so the upper corners use the top-left and top-right radii.
    float radius = (p.y > 0.0)
        ? ((p.x > 0.0) ? InstanceCornerRadii.y : InstanceCornerRadii.x)
        : ((p.x > 0.0) ? InstanceCornerRadii.z : InstanceCornerRadii.w);
    radius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));

    float cornerExp = max(InstanceStyleMeta.x, 2.0);
    float sdf = XRENGINE_UiRoundedBoxSdf(p, halfSize, radius, cornerExp);
    float aa = max(fwidth(sdf), 0.75);
    float fillMask = 1.0 - smoothstep(-aa * 0.5, aa * 0.5, sdf);

    float vTop = clamp(FragLocalPos.y / boxSize.y, 0.0, 1.0);
    vec4 fillColor = ((flags & XRENGINE_UiFlagVerticalGradient) != 0u)
        ? mix(gradBottom, topColor, vTop)
        : topColor;

    vec3 boxRgb = fillColor.rgb;
    float boxAlpha = fillColor.a * fillMask;

    // Frosted glass: saturate and brighten the blurred backdrop, then tint it with the fill.
    if (useBackdrop && fillMask > 0.0)
    {
        float luma = dot(backdrop, vec3(0.2126, 0.7152, 0.0722));
        float sat = InstanceBlurParams.z > 0.0 ? InstanceBlurParams.z : 1.25;
        float bright = InstanceBlurParams.w > 0.0 ? InstanceBlurParams.w : 1.0;
        vec3 vibrant = max(mix(vec3(luma), backdrop, sat) * bright, vec3(0.0));
        vec3 frostedRgb = mix(vibrant, fillColor.rgb, fillColor.a);
        float blurBlend = clamp(InstanceBlurParams.y, 0.0, 1.0);
        boxRgb = mix(fillColor.rgb, frostedRgb, blurBlend);
        boxAlpha = mix(fillColor.a, 1.0, blurBlend) * fillMask;
    }

    // Inner hairline border with a sheen that grows toward the top edge.
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

    // Soft rounded-box drop shadow outside the box silhouette.
    vec4 shadowOut = vec4(0.0);
    if ((flags & XRENGINE_UiFlagDropShadow) != 0u && shadowColor.a > 0.0 &&
        (InstanceShadowParams.z > 0.0 || InstanceShadowParams.w != 0.0))
    {
        vec2 shadowOffset = InstanceShadowParams.xy;
        float shadowBlur = InstanceShadowParams.z;
        float shadowSpread = InstanceShadowParams.w;
        vec2 shadowP = p - vec2(shadowOffset.x, -shadowOffset.y);
        vec2 shadowHalf = max(halfSize + vec2(shadowSpread), vec2(0.5));
        float shadowRadius = clamp(radius + shadowSpread, 0.0, min(shadowHalf.x, shadowHalf.y));
        float shadowSdf = XRENGINE_UiRoundedBoxSdf(shadowP, shadowHalf, shadowRadius, cornerExp);
        float sigma = max(shadowBlur * 0.5, 0.5);
        float t = 1.0 - smoothstep(-sigma * 0.75, sigma * 1.9, shadowSdf);
        float shadowFactor = t * t * (3.0 - 2.0 * t);
        shadowOut = vec4(shadowColor.rgb, shadowColor.a * shadowFactor * (1.0 - fillMask));
    }

    // Straight-alpha composite of the box over its shadow.
    float outAlpha = boxAlpha + shadowOut.a * (1.0 - boxAlpha);
    if (outAlpha <= 1e-5)
    {
        discardFragment = true;
        return vec4(0.0);
    }
    vec3 outRgb = (boxRgb * boxAlpha + shadowOut.rgb * shadowOut.a * (1.0 - boxAlpha)) / outAlpha;
    return vec4(outRgb, outAlpha);
}
