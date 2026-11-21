#ifndef XR_UBER_PROCEDURAL_EYE_GLSL
#define XR_UBER_PROCEDURAL_EYE_GLSL

// Procedural eye: a layered parallax smoke iris, smoke rings, a glare spot, and a dithered
// vignette mask. The forward Uber shader, the native Uber shader, and the splat shader use
// these functions, so every path computes the same eye. The UV convention of the source
// material has V pointing down, so the functions flip the engine V before they evaluate.

float xrProceduralEyeSmoothstep(float edge0, float edge1, float value)
{
    float denom = edge1 - edge0;
    if (abs(denom) < 1.0e-6)
        return value >= edge0 ? 1.0 : 0.0;
    float t = clamp((value - edge0) / denom, 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

mat2 xrProceduralEyeRot(float degreesAngle)
{
    float radiansAngle = radians(degreesAngle);
    float s = sin(radiansAngle);
    float c = cos(radiansAngle);
    // Matches HLSL mul(float2x2(c, -s, s, c), v).
    return mat2(c, s, -s, c);
}

float xrProceduralEyeRand(vec2 uv)
{
    return fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453);
}

vec2 xrProceduralEyeRand22(vec2 uv)
{
    return vec2(xrProceduralEyeRand(vec2(uv.x)), xrProceduralEyeRand(vec2(uv.y)));
}

float xrProceduralEyePerlinNoise(vec2 uv, float offset)
{
    vec2 id = floor(uv);
    vec2 lv = fract(uv);
    float tl = xrProceduralEyeRand(id) * 6.283;
    float tr = xrProceduralEyeRand(id + vec2(1.0, 0.0)) * 6.283;
    float bl = xrProceduralEyeRand(id + vec2(0.0, 1.0)) * 6.283;
    float br = xrProceduralEyeRand(id + vec2(1.0, 1.0)) * 6.283;
    vec2 tlVec = vec2(-sin(tl + offset), cos(tl + offset));
    vec2 trVec = vec2(-sin(tr + offset), cos(tr + offset));
    vec2 blVec = vec2(-sin(bl + offset), cos(bl + offset));
    vec2 brVec = vec2(-sin(br + offset), cos(br + offset));
    float tlDot = dot(tlVec, lv);
    float trDot = dot(trVec, lv - vec2(1.0, 0.0));
    float blDot = dot(blVec, lv - vec2(0.0, 1.0));
    float brDot = dot(brVec, lv - vec2(1.0, 1.0));
    vec2 cubic = lv * lv * (3.0 - 2.0 * lv);
    float topMix = mix(tlDot, trDot, cubic.x);
    float bottomMix = mix(blDot, brDot, cubic.x);
    return mix(topMix, bottomMix, cubic.y) + 0.5;
}

float xrProceduralEyeFbm(vec2 uv, int octaves, float offset)
{
    float value = 0.0;
    float normalizeFactor = 0.0;
    float scale = 0.5;
    for (int index = 0; index < 10; ++index)
    {
        if (index >= octaves)
            break;
        value += xrProceduralEyePerlinNoise(uv, offset) * scale;
        normalizeFactor += scale;
        uv *= 2.0;
        scale *= 0.5;
    }
    return value / max(normalizeFactor, 1.0e-6);
}

vec3 xrProceduralEyeHueShift(vec3 color, float hueAdjust)
{
    float angle = hueAdjust * 6.283185307179586;
    const vec3 k = vec3(0.57735, 0.57735, 0.57735);
    float cosAngle = cos(angle);
    return color * cosAngle + cross(k, color) * sin(angle) + k * dot(k, color) * (1.0 - cosAngle);
}

vec2 xrProceduralEyeParallaxUv(vec2 baseUv, vec3 tangentViewNorm, float parallaxScale, float dbpsStrength)
{
    float safeZ = max(tangentViewNorm.z, 0.05);
    float dbps = mix(1.0, xrProceduralEyeSmoothstep(0.0, 1.0, clamp(tangentViewNorm.z, 0.0, 1.0)), clamp(dbpsStrength, 0.0, 1.0));
    vec2 plane = (tangentViewNorm.xy / safeZ) * dbps;
    return baseUv + plane * parallaxScale;
}

vec2 xrProceduralEyeToSourceUv(vec2 engineUv, vec4 mainTexSt)
{
    vec2 sourceRaw = vec2(engineUv.x, 1.0 - engineUv.y);
    return sourceRaw * mainTexSt.xy + mainTexSt.zw;
}

float xrProceduralEyeCoverageAlpha(vec2 engineUv, vec4 mainTexSt, vec4 params3)
{
    float vignetteTransparency = clamp(params3.z, 0.0, 1.0);
    if (vignetteTransparency <= 0.0001)
        return 1.0;

    vec2 uv = xrProceduralEyeToSourceUv(engineUv, mainTexSt);
    float vignetteThickness = params3.x;
    float vignetteFalloff = params3.y;
    float vignetteDitherScale = params3.w;
    float vignetteMask = xrProceduralEyeSmoothstep(
        vignetteFalloff + 0.001 + vignetteThickness,
        -vignetteFalloff + vignetteThickness,
        length(uv - vec2(0.5)));
    float ditherPass = length(fract(uv * (10000.0 * vignetteDitherScale)) - vec2(0.5)) < vignetteMask ? 1.0 : 0.0;
    return mix(1.0, ditherPass, vignetteTransparency);
}

vec3 xrEvaluateProceduralEye(
    vec2 engineUv,
    vec4 mainTexSt,
    vec3 tangentViewNorm,
    vec3 mainColor,
    vec4 params0,
    vec4 params1,
    vec4 params2,
    vec4 params3,
    float timeSeconds)
{
    vec2 uv = xrProceduralEyeToSourceUv(engineUv, mainTexSt);
    float dbpsStrength = params0.x;
    float mainParallax = params0.y;
    float snakePupil = clamp(params0.z, 0.0, 1.0);
    float pupilSmokeSize = max(params0.w, 1.0e-4);
    float pupilSize = max(params1.x, 1.0e-4);
    float pupilThickness = params1.y;
    float mainHueShift = params1.z;
    float mainHueSpeed = params1.w;
    int ringCount = clamp(int(params2.x + 0.5), 0, 20);
    float ringThickness = params2.y;
    float ringSize = params2.z;
    float vignetteThickness = params3.x;
    float vignetteFalloff = params3.y;

    // The source time vector is (t/20, t, t*2, t*3); these values match its use.
    vec2 time2 = vec2(timeSeconds * 0.1, timeSeconds * 2.0);
    float timeX20 = timeSeconds;
    float timeRotDeg = timeSeconds * 2.5;
    mat2 rotMat = xrProceduralEyeRot(timeRotDeg);

    const int inter = 15;
    const float backSmokeParallax = 0.2;
    const float backSmokeScale = 10.0;
    const float pupilParallax = 0.12;

    float finalPupilCol = 0.0;
    vec2 centeredUv = uv - vec2(0.5);
    for (int r = 0; r < inter; ++r)
    {
        float layerParallax = -backSmokeParallax + (float(r) * pupilParallax) / float(inter);
        vec2 tmpUvOri = xrProceduralEyeParallaxUv(centeredUv, tangentViewNorm, layerParallax, dbpsStrength);
        float snakeDist = length((abs(tmpUvOri) + vec2(0.1, 0.0)) * vec2(1.0 / 0.9, 0.3 / 0.5) / (pupilSmokeSize * 2.0));
        float pupilMaskSnake = xrProceduralEyeSmoothstep(0.25, 0.0, snakeDist);
        float roundDist = length(tmpUvOri / (pupilSmokeSize * 2.0));
        float pupilMaskRound = xrProceduralEyeSmoothstep(1.0, 0.0, xrProceduralEyeSmoothstep(0.01, 0.7, roundDist));
        float pupilMask = mix(pupilMaskRound, pupilMaskSnake, snakePupil);
        vec2 warp = vec2(
            xrProceduralEyeFbm(tmpUvOri * 5.0 + time2, 2, 0.0),
            xrProceduralEyeFbm(tmpUvOri * 10.0 + time2, 2, 0.0)) * 0.3;
        float denom = max(abs(xrProceduralEyeFbm(tmpUvOri * backSmokeScale + warp, 10, timeX20 + float(r) * 0.2) - 0.5), 1.0e-4);
        float smoke = clamp((0.2 / denom) * pupilMask, 0.0, 1.0);
        finalPupilCol += smoke * (1.0 / float(inter));
    }

    float colScalar = finalPupilCol;
    vec2 outlineUv = xrProceduralEyeParallaxUv(centeredUv, tangentViewNorm, -backSmokeParallax, dbpsStrength);
    float snakeOutline = abs(length((abs(outlineUv) + vec2(0.1, 0.0)) * vec2(1.0 / 0.9, 0.3 / 0.5) / (pupilSize * 2.0)) - 0.2);
    float roundOutline = xrProceduralEyeSmoothstep(0.06, 0.6, length(outlineUv / (pupilSize * 2.0)));
    float outline = max(mix(roundOutline, snakeOutline, snakePupil), 1.0e-4);
    vec2 outlineWarp = vec2(
        xrProceduralEyeFbm(outlineUv * 5.0 + time2, 2, 0.0),
        xrProceduralEyeFbm(outlineUv * 10.0 + time2, 2, 0.0)) * 0.3;
    vec2 rotatedOutlineUv = rotMat * (outlineUv * 5.0 + outlineWarp);
    float outlineFbm = xrProceduralEyeFbm(rotatedOutlineUv, 10, timeSeconds);
    float outlineSmoke = 0.02 * pupilThickness / max(abs(outlineFbm - 0.5), 1.0e-4);
    colScalar += clamp((outlineSmoke / outline) * (0.001 / outline), 0.0, 3.0);

    if (ringCount > 0)
    {
        float circleSmoke = 0.02 * ringThickness / max(abs(outlineFbm), 1.0e-4);
        float finalCircleCol = 0.0;
        float invRingCount = 1.0 / float(ringCount);
        for (int e = 0; e < 20; ++e)
        {
            if (e >= ringCount)
                break;
            float ef = float(e);
            vec2 circleUv = xrProceduralEyeParallaxUv(centeredUv, tangentViewNorm, -0.25 * invRingCount * ef, dbpsStrength);
            vec2 offs = (xrProceduralEyeRand22(vec2(ef + 1.0, ef * ef + 1.0)) - vec2(0.5)) * 0.1;
            offs += vec2(sin(timeSeconds + ef), cos(timeSeconds + ef)) * 0.02;
            float circleCol = max(abs(length(circleUv - offs) - 0.6 * ringSize), 1.0e-4);
            finalCircleCol += circleSmoke / circleCol;
        }
        colScalar += clamp(finalCircleCol, 0.0, 2.0);
    }

    vec3 col = vec3(colScalar) * mainColor;
    vec2 glare = xrProceduralEyeParallaxUv(uv, tangentViewNorm, -0.15 * mainParallax, dbpsStrength);
    float glareDist = max(length((glare - vec2(0.5, 0.35)) * vec2(0.4, 0.8)), 1.0e-4);
    col *= clamp(0.1 / glareDist, 0.0, 1.0);

    float vignetteMask = xrProceduralEyeSmoothstep(
        vignetteFalloff + 0.001 + vignetteThickness,
        -vignetteFalloff + vignetteThickness,
        length(centeredUv));
    col *= vignetteMask;
    col = pow(max(col, vec3(0.0)), vec3(2.2));
    col = xrProceduralEyeHueShift(col, mainHueShift + timeSeconds * mainHueSpeed);
    return max(col, vec3(0.0));
}

#endif
