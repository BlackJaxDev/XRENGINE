#ifndef XR_SOURCE_GLITTER_GLSL
#define XR_SOURCE_GLITTER_GLSL

// The caller supplies material access macros and explicit surface footprints.
// Keep random cell locations fixed. Time changes flake orientation, not identity.
vec3 xrGlitterHash(vec2 seed)
{
    vec3 value = fract(vec3(seed.xyx) * 0.1031);
    value += dot(value, value.yzx + 33.33);
    return value;
}

vec2 xrGlitterPoint(vec2 seed)
{
    vec3 value = xrGlitterHash(seed);
    return fract((value.xx + value.yz) * value.zy);
}

float xrGlitterRandom(vec2 seed)
{
    vec3 value = xrGlitterHash(seed);
    return fract((value.x + value.y) * value.z);
}

// Return the stable random seed and nearest-site distance.
vec3 xrGlitterCell(vec2 position, float layer, out vec2 displacement)
{
    vec2 cell = floor(position), offset = fract(position);
    vec2 nearest = vec2(0.0);
    displacement = vec2(0.0);
    float distanceSquared = 100.0;
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
        {
            vec2 neighbor = vec2(x, y);
            vec2 point = xrGlitterPoint(cell + neighbor + layer * 0.5141);
            vec2 delta = neighbor + point * XR_GLITTER_F(_GlitterRandomLocation) - offset;
            float candidate = dot(delta, delta);
            if (candidate < distanceSquared)
            {
                distanceSquared = candidate;
                nearest = point;
                displacement = delta;
            }
        }
    return vec3(nearest, sqrt(distanceSquared));
}

vec3 xrGlitterAngles(vec2 seed, float time)
{
    float speed = XR_GLITTER_F(_GlitterSpeed);
    float range = XR_GLITTER_F(_GlitterAngleRange);
    if (speed <= 0.0)
    {
        vec3 value = xrGlitterHash(seed);
        return (fract((value.xxy + value.yzz) * value.zyx) * 2.0 - 1.0) * range;
    }
    vec3 phase = fract(sin(vec3(dot(seed, vec2(12.9898, 78.233)),
        dot(seed.yx, vec2(12.9898, 78.233)),
        dot(vec2(seed.x * seed.y, seed.y + seed.x), vec2(12.9898, 78.233)))) * 43758.5453) * 2.0 - 1.0;
    return sin((vec3(time * 0.05) + phase * 3.14159265359) * (1.0 + speed)) * range;
}

vec3 xrGlitterRotate(vec3 normal, vec3 degrees)
{
    // Convert source world-axis rotation through the engine Z reflection.
    degrees.xy = -degrees.xy;
    vec3 c = cos(radians(degrees)), s = sin(radians(degrees));
    // Source order is Rx * Ry * Rz with a column vector.
    normal.xy = vec2(c.z * normal.x - s.z * normal.y, s.z * normal.x + c.z * normal.y);
    normal.xz = vec2(c.y * normal.x + s.y * normal.z, -s.y * normal.x + c.y * normal.z);
    normal.yz = vec2(c.x * normal.y - s.x * normal.z, s.x * normal.y + c.x * normal.z);
    return normal / max(length(normal), 1.0e-6);
}

void xrApplySourceGlitter(vec2 uv, vec2 dx, vec2 dy,
    vec2 shapeDx, vec2 shapeDy, float time,
    vec3 geometricNormal, vec3 shadingNormal, vec3 viewDirection,
    vec3 lightDirection, vec3 lightColor, float rampedLight, float directLuminance,
    vec4 tint, vec3 colorMap, float mask, inout vec3 baseColor, inout vec3 emission)
{
    float frequency = XR_GLITTER_F(_GlitterFrequency);
    vec2 position = (uv + XR_GLITTER_V2(_GlitterUVPanning) * time * 0.05) * frequency;
    dx *= frequency;
    dy *= frequency;
    bool source10 = XR_GLITTER_I(_SourceGlitterVersion) == 100;
    vec2 footprint = abs(dx) + abs(dy);
#ifdef XR_GLITTER_FRAGMENT_DERIVATIVES
    footprint = fwidth(position);
#endif
    float sourceWidth = clamp(length(footprint), 0.0001, 1.0);
    if (source10 && XR_GLITTER_F(_GlitterDistanceLOD) > 0.5)
    {
        // Reduce cell density from the unmodified surface footprint. Keep the
        // source cell hash and offset so the two shading paths select one grid.
        vec2 seed = floor(position / floor(footprint + vec2(3.0)));
        float jitter = fract(sin(dot(seed, vec2(12.9898, 78.233))) * 46203.4357) + 0.5;
        vec2 lodScale = floor(footprint + jitter * 0.5);
        vec2 divisor = max(vec2(1.0), lodScale);
        position = position / divisor + frequency * lodScale;
        dx /= divisor;
        dy /= divisor;
    }
    vec3 normal = mix(geometricNormal, shadingNormal, XR_GLITTER_F(_GlitterUseNormals));
    mask *= tint.a * mix(1.0, rampedLight, XR_GLITTER_F(_GlitterHideInShadow)) *
        mix(1.0, directLuminance, XR_GLITTER_F(_GlitterScaleWithLighting));
    if (mask <= 0.0 || (XR_GLITTER_I(_GlitterBlendType) == 0 && XR_GLITTER_F(_GlitterBrightness) <= 0.0))
        return;

    for (int layer = 0; layer < min(XR_GLITTER_I(_GlitterLayers), source10 ? 4 : 8); ++layer)
    {
        vec2 displacement;
        vec3 cell = xrGlitterCell(position, float(layer), displacement);
        // Re-evaluate adjacent surface samples across cell boundaries. A derivative
        // of only the current site is invalid when many flakes cover one pixel.
        vec2 adjacentDisplacement;
        float width = abs(xrGlitterCell(position + dx, float(layer), adjacentDisplacement).z - cell.z) +
            abs(xrGlitterCell(position + dy, float(layer), adjacentDisplacement).z - cell.z);
        if (source10)
            width = sourceWidth;
        float alpha = clamp((XR_GLITTER_F(_GlitterSize) - cell.z) / clamp(width, 0.0001, 1.0), 0.0, 1.0);
        int mode = XR_GLITTER_I(_GlitterMode);
        float strength;
        vec3 color = tint.rgb * mix(vec3(1.0), baseColor, XR_GLITTER_F(_GlitterUseSurfaceColor)) * colorMap;
        vec2 shapeUv = clamp(displacement / max(XR_GLITTER_F(_GlitterSize) * 2.0, 1.0e-6) + 0.5,
            vec2(0.0), vec2(1.0));
        vec4 shapeTransform = XR_GLITTER_V4(_GlitterTexture_ST);
        shapeUv = shapeUv * shapeTransform.xy + shapeTransform.zw +
            XR_GLITTER_V2(_GlitterTexturePan) * time * 0.05;
        vec4 shapeSample = XR_GLITTER_SAMPLE(shapeUv, shapeDx, shapeDy);
        color *= shapeSample.rgb;
        float layerMask = mask * shapeSample.a;
        if (mode == 1)
        {
            float pulse = sin((time * 0.5 + xrGlitterRandom(cell.xy)) * XR_GLITTER_F(_GlitterSpeed)) * 0.5 + 0.5;
            strength = max(XR_GLITTER_F(_GlitterMinBrightness) * alpha,
                pulse * alpha * smoothstep(0.0, 1.0, 1.0 - cell.z * XR_GLITTER_F(_GlitterCenterSize) * 10.0));
        }
        else
        {
            vec3 rotated = xrGlitterRotate(normal, xrGlitterAngles(cell.xy, time));
            float alignment;
            if (mode == 2)
            {
                alpha *= clamp(dot(shadingNormal, lightDirection), 0.0, 1.0);
                vec3 halfDirection = lightDirection + viewDirection;
                halfDirection /= max(length(halfDirection), 1.0e-6);
                alignment = max(dot(halfDirection, rotated), 0.0);
                color *= lightColor;
            }
            else
                alignment = clamp(dot(mix(rotated, viewDirection, XR_GLITTER_F(_GlitterBias)), viewDirection), 0.0, 1.0);
            strength = (XR_GLITTER_F(_GlitterMinBrightness) * alpha * alpha +
                pow(alignment, XR_GLITTER_F(_GlitterContrast))) * alpha;
        }
        float brightness = XR_GLITTER_F(_GlitterBrightness);
        if (XR_GLITTER_I(_GlitterBlendType) == 1)
        {
            baseColor = mix(baseColor, strength * color * brightness, strength * layerMask);
            emission += strength * color * max(brightness - 1.0, 0.0) * layerMask;
        }
        else
            emission += strength * color * brightness * layerMask;
    }
}

vec2 xrGlitter81Random2(vec2 seed)
{
    return fract(sin(vec2(dot(seed, vec2(127.1, 311.7)),
        dot(seed, vec2(269.5, 183.3)))) * 43758.5453);
}

vec3 xrGlitter81Cell(vec2 position, out vec2 displacement)
{
    vec2 cell = floor(position);
    vec2 offset = fract(position);
    vec2 seed = vec2(0.0);
    float distanceSquared = 100.0;
    displacement = vec2(0.0);
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
        {
            vec2 neighbor = vec2(x, y);
            vec2 randomPoint = xrGlitter81Random2(cell + neighbor);
            vec2 point = 0.5 + 0.5 * sin(6.2831 * randomPoint);
            vec2 delta = neighbor + point - offset;
            float candidate = dot(delta, delta);
            if (candidate < distanceSquared)
            {
                distanceSquared = candidate;
                displacement = delta;
                seed = randomPoint;
            }
        }
    return vec3(seed, sqrt(distanceSquared));
}

void xrApplySourceGlitter81(vec2 uv, vec2 dx, vec2 dy, float time,
    vec3 geometricNormal, vec3 viewDirection, vec4 tint, float mask,
    inout vec3 emission)
{
    float frequency = XR_GLITTER_F(_GlitterFrequency);
    vec2 position = fract(uv) * frequency;
    vec2 displacement;
    vec3 cell = xrGlitter81Cell(position, displacement);
#ifdef XR_GLITTER_FRAGMENT_DERIVATIVES
    float width = fwidth(cell.z);
#else
    vec2 adjacentDisplacement;
    float width = abs(xrGlitter81Cell(position + dx * frequency, adjacentDisplacement).z - cell.z) +
        abs(xrGlitter81Cell(position + dy * frequency, adjacentDisplacement).z - cell.z);
#endif
    float size = XR_GLITTER_F(_GlitterSize);
    float alpha = clamp((size - cell.z) / clamp(width, 0.0001, 1.0), 0.0, 1.0);
    vec3 rotated = xrGlitterRotate(geometricNormal, xrGlitterAngles(cell.xy, time));
    float alignment = clamp(dot(mix(rotated, viewDirection, XR_GLITTER_F(_GlitterBias)),
        viewDirection), 0.0, 1.0);
    float strength = pow(alignment, XR_GLITTER_F(_GlitterContrast)) * alpha;
    vec2 shapeUv = clamp(displacement / max(2.0 * size, 1.0e-6) + 0.5,
        vec2(0.0), vec2(1.0));
    vec4 shapeTransform = XR_GLITTER_V4(_GlitterTexture_ST);
    shapeUv = shapeUv * shapeTransform.xy + shapeTransform.zw +
        XR_GLITTER_V2(_GlitterTexturePan) * time * 0.05;
    vec4 shapeSample = XR_GLITTER_SAMPLE(shapeUv, dx, dy);
    emission += strength * tint.rgb * shapeSample.rgb * XR_GLITTER_F(_GlitterBrightness) *
        shapeSample.a * tint.a * mask;
}

#endif
