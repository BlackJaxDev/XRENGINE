#ifndef XR_SOURCE_FLIPBOOK_GLSL
#define XR_SOURCE_FLIPBOOK_GLSL

// The caller supplies exact array dimensions, gradients, and material access.
// The caller supplies engine UVs, which have V flipped relative to the source.
// Placement math runs in source UV space. Array layers keep source row order.
float xrFlipbookRemainder(float value, float divisor)
{
    return value - trunc(value / divisor) * divisor;
}

void xrApplySourceFlipbook(vec2 uv, vec2 dx, vec2 dy, vec3 dimensions,
    float mask, float time, bool shadowPass, inout vec4 base, inout vec3 emission)
{
    int alphaMode = XR_FLIP_I(_FlipbookAlphaControlsFinalAlpha);
    if (shadowPass && alphaMode != 1)
        return;
    uv.y = 1.0 - uv.y;
    dx.y = -dx.y;
    dy.y = -dy.y;
    vec4 placement = XR_FLIP_V(_SourceFlipbookLayout);
    vec4 options = XR_FLIP_V(_SourceFlipbookOptions);
    vec4 motion = XR_FLIP_V(_SourceFlipbookMotion);
    vec4 flags = XR_FLIP_V(_SourceFlipbookFlags);
    vec4 sides = XR_FLIP_V(_SourceFlipbookSides) * vec4(-1.0, 1.0, -1.0, 1.0);
    vec2 scale = placement.xy;
    if (options.w > 0.5)
    {
        vec2 ratio = dimensions.xy / max(options.w < 1.5
            ? max(dimensions.x, dimensions.y) : min(dimensions.x, dimensions.y), 1.0);
        scale *= ratio;
    }
    if (motion.z > 0.5 && motion.z < 1.5)
    {
        float signX = uv.x < 0.5 ? -1.0 : 1.0;
        uv.x = abs(uv.x - 0.5) + 0.5;
        dx.x *= signX;
        dy.x *= signX;
    }
    else if (motion.z > 1.5 && uv.x < 0.5)
        uv.x += 0.5;
    vec2 center = placement.zw + (sides.xz + sides.yw) * 0.5;
    float angle = radians(motion.x + time * 2.0 * motion.y);
    mat2 rotation = mat2(cos(angle), sin(angle), -sin(angle), cos(angle));
    vec2 extent = scale + sides.yw - sides.xz;
    if (any(lessThanEqual(abs(extent), vec2(1.0e-7))))
        return;
    uv = (rotation * (uv - center) + center - placement.zw - sides.xz + scale * 0.5) / extent;
    dx = rotation * dx / extent;
    dy = rotation * dy / extent;
    float edge = 1.0;
    if (motion.w < 0.5 || motion.w > 1.5)
    {
        vec2 edgeUv = motion.w > 1.5 ? fract(uv) : uv;
        vec2 width = abs(dx) + abs(dy) + vec2(1.0e-7);
        vec2 factor = (1.0 - abs(edgeUv * 2.0 - 1.0)) / width;
        edge = clamp(min(factor.x, factor.y), 0.0, 1.0);
    }
    float count = dimensions.z;
    float start = XR_FLIP_F(_FlipbookStartFrame);
    if (flags.x > 0.5)
        count = min(max(start, XR_FLIP_F(_FlipbookEndFrame)), count) - max(0.0, start);
    if (count <= 0.0)
        return;
    float frame = 0.0;
    if (XR_FLIP_F(_FlipbookManualFrameControl) > 0.5)
        frame = xrFlipbookRemainder(XR_FLIP_F(_FlipbookCurrentFrame), count);
    else if (XR_FLIP_F(_FlipbookFPS) != 0.0)
        frame = xrFlipbookRemainder(time * XR_FLIP_F(_FlipbookFPS) + XR_FLIP_F(_FlipbookFrameOffset), count) +
            (flags.x > 0.5 ? start : 0.0);
    vec4 st = XR_FLIP_V(_FlipbookScaleOffset);
    uv = uv * st.xy + st.zw + XR_FLIP_V2(_FlipbookTexArrayPan) * time * 0.05;
    dx *= st.xy;
    dy *= st.xy;
    vec4 sampleValue = XR_FLIP_SAMPLE(uv, floor(frame), dx, dy);
    if (XR_FLIP_F(_FlipbookCrossfadeEnabled) > 0.5)
    {
        float wrap = count + (flags.x > 0.5 ? max(start, 0.0) : 0.0);
        vec2 range = XR_FLIP_V2(_FlipbookCrossfadeRange);
        float blend = smoothstep(range.x, max(range.y, range.x + 1.0e-7), fract(frame));
        sampleValue = mix(sampleValue, XR_FLIP_SAMPLE(uv, floor(xrFlipbookRemainder(frame + 1.0, wrap)), dx, dy), blend);
    }
    sampleValue.a = (options.z > 0.5 ? max(max(sampleValue.r, sampleValue.g), sampleValue.b) : sampleValue.a) * edge;
    vec4 tint = XR_FLIP_V(_FlipbookColor);
    sampleValue.rgb = options.y > 0.5 ? tint.rgb : sampleValue.rgb * tint.rgb;
    vec4 hue = XR_FLIP_V(_SourceFlipbookHue);
    if (hue.x > 0.5)
    {
        float shift = hue.y + hue.z * time * 0.05;
        vec3 hsv = XR_FLIP_RGB_TO_HSV(sampleValue.rgb);
        hsv.x = hsv.x * flags.y + shift;
        vec3 lab = XR_FLIP_RGB_TO_OKLAB(max(sampleValue.rgb, vec3(0.0)));
        if (flags.y < 0.5)
            lab.yz = vec2(length(lab.yz), 0.0);
        float hueAngle = shift * 6.28318530718;
        lab.yz = mat2(cos(hueAngle), sin(hueAngle), -sin(hueAngle), cos(hueAngle)) * lab.yz;
        sampleValue.rgb = mix(XR_FLIP_OKLAB_TO_RGB(lab), XR_FLIP_HSV_TO_RGB(hsv), clamp(hue.w, 0.0, 1.0));
    }
    float alpha = clamp(sampleValue.a * tint.a * clamp(options.x, 0.0, 1.0), 0.0, 1.0);
    float maskedAlpha = clamp(alpha * mask, 0.0, 1.0);
    if (!shadowPass)
    {
        if (alphaMode == 2)
            base = vec4(sampleValue.rgb, maskedAlpha);
        else
            base.rgb = mix(base.rgb, XR_FLIP_BLEND(base.rgb, sampleValue.rgb, XR_FLIP_I(_FlipbookBlendType)), maskedAlpha);
        emission += sampleValue.rgb * XR_FLIP_F(_FlipbookEmissionStrength) * maskedAlpha;
    }
    if (alphaMode == 1)
        base.a = mix(base.a, alpha, mask);
}

#endif
