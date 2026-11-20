#ifndef XRENGINE_SURFACE_EXTENSIONS_GLSL
#define XRENGINE_SURFACE_EXTENSIONS_GLSL

// Native implementations for advanced surface, lighting, repeated-layer,
// and texture-array behavior. Importers own all source-format translation.

float uberHash21(vec2 value)
{
    vec3 p3 = fract(vec3(value.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float uberChannel(vec4 value, int channel)
{
    return value[clamp(channel, 0, 3)];
}

vec3 uberBlendRgb(vec3 baseColor, vec3 layerColor, int mode)
{
    if (mode == BLEND_DARKEN)
        return min(baseColor, layerColor);
    if (mode == BLEND_MULTIPLY)
        return baseColor * layerColor;
    if (mode == BLEND_LIGHTEN)
        return max(baseColor, layerColor);
    if (mode == BLEND_SCREEN)
        return blendScreen(baseColor, layerColor);
    if (mode == BLEND_SUBTRACT)
        return baseColor - layerColor;
    if (mode == BLEND_ADD)
        return baseColor + layerColor;
    if (mode == BLEND_OVERLAY)
        return blendOverlay(baseColor, layerColor);
    if (mode == BLEND_MIXED)
        return mix(baseColor * layerColor, blendScreen(baseColor, layerColor), layerColor);
    return layerColor;
}

vec2 uberResolveUv(int uvMode, ToonMesh mesh)
{
    return getUV(uvMode, mesh);
}

#ifndef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
// Stochastic sampling hides texture repetition. Each pixel blends three samples
// whose UV offsets come from the corners of a skewed triangle grid. Deliot-Heitz
// blends with the barycentric weights. Hextile can also rotate each tile, and it
// blends with weights that favor the brighter sample.
vec2 uberStochasticHash(vec2 cell)
{
    vec2 seed = vec2(dot(cell, vec2(127.1, 311.7)), dot(cell, vec2(269.5, 183.3)));
    return fract(sin(mod(seed, 3.14159)) * 43758.5453);
}

// Skews UV into a triangle grid whose cell edge is about 1 / (2 * sqrt(3)) of a UV unit at density 1.
vec2 uberStochasticSkew(vec2 uv, float density)
{
    vec2 scaled = uv * 3.4641 * density;
    return vec2(scaled.x - 0.57735027 * scaled.y, 1.15470054 * scaled.y);
}

vec4 uberSampleDeliotHeitz(sampler2D source, vec2 uv, vec2 dx, vec2 dy)
{
    vec2 skewed = uberStochasticSkew(uv, _StochasticDeliotHeitzDensity);
    vec2 cell = floor(skewed);
    vec2 local = fract(skewed);
    float remainder = 1.0 - local.x - local.y;

    vec2 cell0 = cell;
    vec2 cell1 = cell + vec2(0.0, 1.0);
    vec2 cell2 = cell + vec2(1.0, 0.0);
    vec3 weights = vec3(remainder, local.y, local.x);
    if (remainder <= 0.0)
    {
        cell0 = cell + vec2(1.0);
        cell1 = cell + vec2(1.0, 0.0);
        cell2 = cell + vec2(0.0, 1.0);
        weights = vec3(-remainder, 1.0 - local.y, 1.0 - local.x);
    }

    return textureGrad(source, uv + uberStochasticHash(cell0), dx, dy) * weights.x
         + textureGrad(source, uv + uberStochasticHash(cell1), dx, dy) * weights.y
         + textureGrad(source, uv + uberStochasticHash(cell2), dx, dy) * weights.z;
}

// Returns (cos, sin) of the tile rotation. The angle depends only on the tile vertex.
vec2 uberHextileRotation(vec2 vertex)
{
    // The snippets undefine PI, so this function uses TWO_PI.
    float angle = abs(vertex.x * vertex.y) + abs(vertex.x + vertex.y) + 0.5 * TWO_PI;
    angle = mod(angle, TWO_PI);
    if (angle > 0.5 * TWO_PI)
        angle -= TWO_PI;
    angle *= _StochasticHexRotationStrength;
    return vec2(cos(angle), sin(angle));
}

// Rotates a row vector by the tile rotation.
vec2 uberHextileRotate(vec2 value, vec2 rotation)
{
    return vec2(
        value.x * rotation.x + value.y * rotation.y,
        value.y * rotation.x - value.x * rotation.y);
}

vec4 uberSampleHextileTile(sampler2D source, vec2 uv, vec2 vertex, vec2 dx, vec2 dy)
{
    vec2 center = vec2(vertex.x + 0.5 * vertex.y, vertex.y / 1.15470054) * 0.288675;
    vec2 rotation = _StochasticHexRotationStrength > 0.0 ? uberHextileRotation(vertex) : vec2(1.0, 0.0);
    vec2 tileUv = uberHextileRotate(uv - center, rotation) + center + uberStochasticHash(vertex);
    return textureGrad(source, tileUv, uberHextileRotate(dx, rotation), uberHextileRotate(dy, rotation));
}

vec4 uberSampleHextile(sampler2D source, vec2 uv, vec2 dx, vec2 dy)
{
    vec2 skewed = uberStochasticSkew(uv, _StochasticHexGridDensity);
    vec2 cell = floor(skewed);
    vec3 local = vec3(fract(skewed), 0.0);
    local.z = 1.0 - local.x - local.y;

    float upper = step(0.0, -local.z);
    float sign2 = 2.0 * upper - 1.0;
    vec3 weights = vec3(-local.z * sign2, upper - local.y * sign2, upper - local.x * sign2);

    vec4 c0 = uberSampleHextileTile(source, uv, cell + vec2(upper, upper), dx, dy);
    vec4 c1 = uberSampleHextileTile(source, uv, cell + vec2(upper, 1.0 - upper), dx, dy);
    vec4 c2 = uberSampleHextileTile(source, uv, cell + vec2(1.0 - upper, upper), dx, dy);

    const vec3 lumaWeights = vec3(0.299, 0.587, 0.114);
    vec3 luma = vec3(dot(c0.rgb, lumaWeights), dot(c1.rgb, lumaWeights), dot(c2.rgb, lumaWeights));
    luma = mix(vec3(1.0), luma, _StochasticHexFallOffContrast);
    weights = luma * pow(max(weights, vec3(0.0)), vec3(_StochasticHexFallOffPower));
    weights /= max(weights.x + weights.y + weights.z, 1e-6);
    return c0 * weights.x + c1 * weights.y + c2 * weights.z;
}

// Pixel mode snaps UV to texel centers and keeps a one-pixel smooth edge between texels.
vec2 uberSharpSample(vec2 uv, vec2 textureSizePixels)
{
    vec2 p = uv * textureSizePixels;
    vec2 edge = max(fwidth(p), vec2(1e-6));
    p = floor(p) + saturate(fract(p) / edge);
    return (p - 0.5) / textureSizePixels;
}
#endif

// Samples with the material stochastic mode when the texture enables it.
vec4 uberSampleStochastic(sampler2D source, vec2 uv, float enabled)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return texture(source, uv);
#else
    if (enabled < 0.5 || _StochasticMode == 2)
        return texture(source, uv);

    vec2 dx = dFdx(uv);
    vec2 dy = dFdy(uv);
    return _StochasticMode == 0
        ? uberSampleDeliotHeitz(source, uv, dx, dy)
        : uberSampleHextile(source, uv, dx, dy);
#endif
}

vec2 uberMainUv(ToonMesh mesh)
{
    vec2 uv = getUV(_MainTexUV, mesh);
#ifndef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    uv = rotateUV(uv, radians(_MainTexRotation), vec2(0.5));
    vec2 distortionUv = transformUV(
        uberResolveUv(_MainTexDistortionMapUV, mesh),
        _MainTexDistortionMap_ST);
    distortionUv += _MainTexDistortionSpeed * u_Time;
    vec2 maskUv = transformUV(
        uberResolveUv(_MainTexDistortionMaskUV, mesh),
        _MainTexDistortionMask_ST);
    vec2 distortion = texture(_MainTexDistortionMap, distortionUv).rg * 2.0 - 1.0;
    float distortionMask = texture(_MainTexDistortionMask, maskUv).r;
    uv += distortion * (_MainTexDistortionStrength * distortionMask);
    uv = transformUV(uv, _MainTex_ST);
    if (_MainPixelMode > 0.5)
        uv = uberSharpSample(uv, vec2(textureSize(_MainTex, 0)));
    return panUV(uv, _MainTexPan, u_Time);
#else
    return panUV(transformUV(uv, _MainTex_ST), _MainTexPan, u_Time);
#endif
}

vec4 uberSampleMainTexture(ToonMesh mesh)
{
    vec2 uv = uberMainUv(mesh);
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return texture(_MainTex, uv);
#else
    vec4 color = uberSampleStochastic(_MainTex, uv, _MainTexStochastic);
    color.a = max(color.a, _MainIgnoreTexAlpha);
    return color;
#endif
}

vec3 uberApplyExtendedColorAdjustments(vec3 color)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return color;
#else
    color = (color - 0.5) * max(_MainContrast, 0.0) + 0.5;
    color = mix(color, vec3(luminance(color)), saturate(_MainGrayscale));
    color = mix(color, _MainColorReplace.rgb, saturate(_MainColorReplaceStrength) * _MainColorReplace.a);
    return color;
#endif
}

vec3 uberResolveThemeColor(int themeIndex, vec3 authoredColor)
{
#ifdef XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES
    return authoredColor;
#else
    vec4 theme = vec4(authoredColor, 1.0);
    vec3 adjustment = vec3(0.0);
    if (themeIndex == 1) { theme = _GlobalThemeColor0; adjustment = _GlobalThemeAdjust0; }
    else if (themeIndex == 2) { theme = _GlobalThemeColor1; adjustment = _GlobalThemeAdjust1; }
    else if (themeIndex == 3) { theme = _GlobalThemeColor2; adjustment = _GlobalThemeAdjust2; }
    else if (themeIndex == 4) { theme = _GlobalThemeColor3; adjustment = _GlobalThemeAdjust3; }
    else return authoredColor;

    vec3 hsv = rgbToHsv(theme.rgb);
    hsv.x = fract(hsv.x + adjustment.x);
    hsv.y = saturate(hsv.y + adjustment.y);
    hsv.z = saturate(hsv.z + adjustment.z);
    return hsvToRgb(hsv);
#endif
}

vec4 uberSampleGlobalMaskTexture(int textureIndex, ToonMesh mesh, float timeScale)
{
#ifdef XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES
    return vec4(1.0);
#else
    // Global mask pans use the source time unit (0.05 per second), as the native Uber path does.
    vec4 value;
    if (textureIndex == 0)
        value = texture(_GlobalMaskTexture0, panUV(transformUV(getUV(_GlobalMaskTexture0UV, mesh), _GlobalMaskTexture0_ST), _GlobalMaskTexture0Pan, u_Time * timeScale));
    else if (textureIndex == 1)
        value = texture(_GlobalMaskTexture1, panUV(transformUV(getUV(_GlobalMaskTexture1UV, mesh), _GlobalMaskTexture1_ST), _GlobalMaskTexture1Pan, u_Time * timeScale));
    else if (textureIndex == 2)
        value = texture(_GlobalMaskTexture2, panUV(transformUV(getUV(_GlobalMaskTexture2UV, mesh), _GlobalMaskTexture2_ST), _GlobalMaskTexture2Pan, u_Time * timeScale));
    else
        value = texture(_GlobalMaskTexture3, panUV(transformUV(getUV(_GlobalMaskTexture3UV, mesh), _GlobalMaskTexture3_ST), _GlobalMaskTexture3Pan, u_Time * timeScale));

    vec4 denominator = max(_GlobalMaskMax - _GlobalMaskMin, vec4(EPSILON));
    value = saturate((value - _GlobalMaskMin) / denominator);
    value = mix(value, vec4(1.0) - value, saturate(_GlobalMaskInvert));
    float distanceFade = smoothstep(
        _GlobalMaskDistanceMin,
        max(_GlobalMaskDistanceMax, _GlobalMaskDistanceMin + EPSILON),
        length(mesh.worldPos - CameraPosition));
    value *= mix(vec4(1.0), vec4(distanceFade), saturate(_GlobalMaskDistance));
    float vertexModifier = _GlobalMaskModifiers.x <= 0.0
        ? 1.0
        : mesh.vertexColor[clamp(int(_GlobalMaskModifiers.x) - 1, 0, 3)];
    float backfaceModifier = mix(1.0, step(0.0, mesh.isFrontFace), saturate(_GlobalMaskModifiers.y));
    float mirrorModifier = mix(1.0, step(0.0, mesh.viewDir.x), saturate(_GlobalMaskModifiers.z));
    float cameraModifier = mix(1.0, saturate(dot(mesh.worldNormal, mesh.viewDir)), saturate(_GlobalMaskModifiers.w));
    value *= vertexModifier * backfaceModifier * mirrorModifier * cameraModifier;
    return value;
#endif
}

float uberGlobalMask(int encodedIndex, ToonMesh mesh, float timeScale)
{
    if (encodedIndex <= 0)
        return 1.0;

    // Four RGBA channels across four mask textures are addressed as 1..16.
    // Clamp malformed authored values and avoid a dynamic vector subscript here:
    // NVIDIA's OpenGL compiler can conservatively treat the modulo expression as
    // potentially negative even after the guard above and reject the variant as
    // an out-of-bounds access.
    int zeroBased = clamp(encodedIndex - 1, 0, 15);
    int channel = zeroBased % 4;
    vec4 mask = uberSampleGlobalMaskTexture(zeroBased / 4, mesh, timeScale);
    if (channel == 0)
        return mask.r;
    if (channel == 1)
        return mask.g;
    if (channel == 2)
        return mask.b;
    return mask.a;
}

float uberGlobalMask(int encodedIndex, ToonMesh mesh)
{
    return uberGlobalMask(encodedIndex, mesh, 1.0);
}

float uberBlendMask(float baseMask, float blendMask, int blendType)
{
    if (blendType == 0) return clamp(blendMask, 0.0, 1.0);
    if (blendType == 1) return clamp(baseMask - blendMask, 0.0, 1.0);
    if (blendType == 2) return clamp(baseMask * blendMask, 0.0, 1.0);
    if (blendType == 3) return clamp(baseMask / max(blendMask, 0.0001), 0.0, 1.0);
    if (blendType == 4) return min(baseMask, blendMask);
    if (blendType == 5) return max(baseMask, blendMask);
    if (blendType == 6) return clamp((baseMask + blendMask) * 0.5, 0.0, 1.0);
    return clamp(baseMask + blendMask, 0.0, 1.0);
}

vec3 uberApplyColorMask(vec3 baseColor, ToonMesh mesh, inout vec3 emission, inout PBRData pbr)
{
#ifdef XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES
    return baseColor;
#else
    if (_ColorMaskEnabled < 0.5)
        return baseColor;

    vec2 uv = transformUV(getUV(_ColorMaskUV, mesh), _ColorMask_ST);
    vec4 mask = texture(_ColorMaskTexture, uv);
    vec4 colors[4] = vec4[4](_ColorMaskColor0, _ColorMaskColor1, _ColorMaskColor2, _ColorMaskColor3);
    for (int index = 0; index < 4; ++index)
    {
        float weight = saturate(mask[index] * colors[index].a);
        vec3 themed = uberResolveThemeColor(int(_ColorMaskThemeIndices[index] + 0.5), colors[index].rgb);
        vec3 blended = uberBlendRgb(baseColor, themed, int(_ColorMaskBlendModes[index] + 0.5));
        baseColor = mix(baseColor, blended, weight);
        emission += themed * (_ColorMaskEmission[index] * weight);
        pbr.metallic = mix(pbr.metallic, _ColorMaskMetallic[index], weight);
        pbr.perceptualRoughness = mix(pbr.perceptualRoughness, 1.0 - _ColorMaskSmoothness[index], weight);
    }
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    return baseColor;
#endif
}

vec3 uberApplyColorMaskNormal(vec3 normal, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES
    return normal;
#else
    if (_ColorMaskEnabled < 0.5)
        return normal;

    vec2 uv = transformUV(getUV(_ColorMaskUV, mesh), _ColorMask_ST);
    vec4 mask = texture(_ColorMaskTexture, uv);
    float correction = dot(mask, saturate(_ColorMaskNormalStrength));
    return normalize(mix(normal, mesh.vertexNormal, saturate(correction)));
#endif
}

// Combines two mask values with a blend operation.
// Blend type: 0 replace, 1 subtract, 2 multiply, 3 divide, 4 min, 5 max, 6 average, 7 add.
float uberMaskBlend(float baseMask, float blendMask, int blendType)
{
    float result;
    switch (blendType)
    {
        case 0: result = blendMask; break;
        case 1: result = baseMask - blendMask; break;
        case 2: result = baseMask * blendMask; break;
        case 3: result = baseMask / max(blendMask, 0.0001); break;
        case 4: result = min(baseMask, blendMask); break;
        case 5: result = max(baseMask, blendMask); break;
        case 6: result = (baseMask + blendMask) * 0.5; break;
        default: result = baseMask + blendMask; break;
    }
    return saturate(result);
}

// Hermite step that accepts equal or reversed edges. Equal edges give a hard step at the edge.
float uberEdgeStep(float edge0, float edge1, float x)
{
    float range = edge1 - edge0;
    float t = abs(range) < EPSILON ? step(edge0, x) : saturate((x - edge0) / range);
    return t * t * (3.0 - 2.0 * t);
}

// Lerps from 1 to the value by a global mask. Index 0 keeps the value.
float uberGlobalMaskStrength(float value, int encodedIndex, ToonMesh mesh)
{
    return encodedIndex > 0 ? mix(1.0, value, uberGlobalMask(encodedIndex, mesh)) : value;
}

// Applies the alpha options that run before decals and dissolve: the alpha global mask,
// distance fade, fresnel alpha, and angular alpha.
float uberApplyExtendedAlpha(float alpha, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return alpha;
#else
    if (_AlphaGlobalMask > 0)
        alpha = uberMaskBlend(alpha, uberGlobalMask(_AlphaGlobalMask, mesh), _AlphaGlobalMaskBlendType);

    vec3 objectOrigin = (u_ModelMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    if (_DistanceFade > 0.5)
    {
        vec3 position = _DistanceFadePosition != 0 ? mesh.worldPos : objectOrigin;
        float fade = mix(
            _DistanceFadeNearAlpha,
            _DistanceFadeFarAlpha,
            uberEdgeStep(_DistanceFadeMin, _DistanceFadeMax, distance(position, CameraPosition)));
        alpha *= uberGlobalMaskStrength(fade, _DistanceFadeGlobalMask, mesh);
    }

    if (_AlphaFresnel > 0.5)
    {
        float viewDotNormal = abs(dot(mesh.viewDir, mesh.worldNormal));
        float rim = saturate(1.0 - uberEdgeStep(
            min(_AlphaFresnelSharpness, _AlphaFresnelWidth),
            _AlphaFresnelWidth,
            viewDotNormal));
        rim = abs(mix(1.0, rim, _AlphaFresnelAlpha));
        rim = _AlphaFresnelInvert > 0.5 ? 1.0 - rim : rim;
        alpha *= uberGlobalMaskStrength(rim, _AlphaFresnelGlobalMask, mesh);
    }

    if (_AlphaAngular > 0.5)
    {
        // The camera axis is the camera +Z axis in world space. It points away from the view direction.
        vec3 cameraAxis = normalize(XRENGINE_GetForwardResolvedInverseViewMatrix()[2].xyz);
        vec3 position = _AngleCompareTo == 0 ? objectOrigin : mesh.worldPos;
        vec3 cameraToModel = normalize(position - CameraPosition);
        vec3 modelForward = normalize(mat3(u_ModelMatrix) * normalize(_AngleForwardDirection));
        float cameraFacing = saturate(
            (0.5 * dot(cameraToModel, cameraAxis) + 0.5 - _CameraAngleMax / 180.0) /
            ((_CameraAngleMin - _CameraAngleMax) / 180.0 + EPSILON));
        float modelFacing = saturate(
            (0.5 * dot(-cameraToModel, modelForward) + 0.5 - _ModelAngleMax / 180.0) /
            ((_ModelAngleMin - _ModelAngleMax) / 180.0 + EPSILON));
        float facing = _AngleType == 0 ? cameraFacing
            : _AngleType == 1 ? modelFacing
            : cameraFacing * modelFacing;
        alpha *= uberGlobalMaskStrength(max(facing, _AngleMinAlpha), _AlphaAngularGlobalMask, mesh);
    }
    return saturate(alpha);
#endif
}

// Returns the 8x8 ordered dither threshold of a pixel.
float uberBayerDither(vec2 pixel)
{
    const float bayer[64] = float[64](
        0.015625, 0.765625, 0.203125, 0.953125, 0.06250, 0.81250, 0.25000, 1.00000,
        0.515625, 0.265625, 0.703125, 0.453125, 0.56250, 0.31250, 0.75000, 0.50000,
        0.140625, 0.890625, 0.078125, 0.828125, 0.18750, 0.93750, 0.12500, 0.87500,
        0.640625, 0.390625, 0.578125, 0.328125, 0.68750, 0.43750, 0.62500, 0.37500,
        0.046875, 0.796875, 0.234375, 0.984375, 0.03125, 0.78125, 0.21875, 0.96875,
        0.546875, 0.296875, 0.734375, 0.484375, 0.53125, 0.28125, 0.71875, 0.46875,
        0.171875, 0.921875, 0.109375, 0.859375, 0.15625, 0.90625, 0.09375, 0.84375,
        0.671875, 0.421875, 0.609375, 0.359375, 0.65625, 0.40625, 0.59375, 0.34375);
    ivec2 cell = ivec2(mod(floor(pixel), 8.0));
    return bayer[cell.y * 8 + cell.x];
}

// Applies the alpha options that run directly before the coverage test: sharpened alpha to
// coverage and dithering. The caller applies force opaque.
float uberApplyCoverageAlpha(float alpha, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return alpha;
#else
    if (_Mode == BLEND_MODE_CUTOUT && _AlphaSharpenedA2C > 0.5)
    {
        vec2 texel = mesh.uv[0] * vec2(textureSize(_MainTex, 0));
        vec2 dx = dFdx(texel);
        vec2 dy = dFdy(texel);
        float mipLevel = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), EPSILON));
        alpha *= 1.0 + max(0.0, mipLevel) * _AlphaMipScale;
        alpha = saturate((alpha - _Cutoff) / max(fwidth(alpha), 0.0001) + _Cutoff);
    }

    if (_AlphaDither > 0.5)
    {
        float dither = uberBayerDither(gl_FragCoord.xy) - _AlphaDitherBias;
        alpha = saturate(alpha - dither * (1.0 - alpha) * _AlphaDitherGradient);
    }
    return alpha;
#endif
}

vec3 uberCorrectNormal(vec3 normal, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return normal;
#else
    if (_NormalCorrectStrength <= 0.0)
        return normal;

    // The corrected normal points away from a vertical axis through the origin, in the
    // horizontal plane of the object.
    vec3 horizontal = mesh.localPos - _NormalCorrectOrigin;
    horizontal.y = 0.0;
    if (dot(horizontal, horizontal) <= 1e-12)
        return normal;
    vec3 objectOut = normalize(mat3(u_ModelMatrix) * normalize(horizontal));
    float vertexMask = _NormalCorrectVertexColor == 0
        ? 1.0
        : uberChannelAt(mesh.vertexColor, _NormalCorrectVertexColor - 1);
    return normalize(mix(normal, objectOut, saturate(_NormalCorrectStrength * vertexMask)));
#endif
}

#ifndef XRENGINE_UBER_DISABLE_LAYERED_DECALS
vec4 uberSampleDecal(
    sampler2D decalTexture,
    vec4 decalColor,
    vec2 position,
    vec2 scale,
    vec4 sideOffset,
    vec2 pan,
    float rotation,
    float rotationSpeed,
    int uvMode,
    int maskChannel,
    vec4 modifiers,
    vec4 effects,
    vec4 sourceAlphaParams,
    ToonMesh mesh,
    out vec3 alphaControl)
{
    vec2 uv = getUV(uvMode, mesh);
    int mirrorMode = int(modifiers.z + 0.5);
    if (mirrorMode == 1 || (mirrorMode == 4 && mesh.viewDir.x < 0.0))
        uv.x = 1.0 - uv.x;
    // A side offset moves each edge of the decal rectangle: x and y move the left and right
    // edges, z and w move the bottom and top edges. It shifts the center and changes the span.
    vec4 sideAdjust = vec4(-sideOffset.x, sideOffset.y, -sideOffset.z, sideOffset.w);
    position += 0.5 * vec2(sideAdjust.x + sideAdjust.y, sideAdjust.z + sideAdjust.w);
    scale += vec2(sideAdjust.y - sideAdjust.x, sideAdjust.w - sideAdjust.z);
    uv = panUV(uv, pan, u_Time);
    uv = (uv - position) / max(abs(scale), vec2(EPSILON)) + vec2(0.5);
    uv = rotateUV(uv, radians(rotation + rotationSpeed * u_Time), vec2(0.5));
    float bounds = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
    float strictBounds = float(uv.x > 0.0 && uv.x < 1.0 && uv.y > 0.0 && uv.y < 1.0);
    vec4 sampled = texture(decalTexture, uv);
    if (effects.z != 0.0)
    {
        vec2 chromaOffset = vec2(effects.z) / vec2(textureSize(decalTexture, 0));
        sampled.r = texture(decalTexture, uv + chromaOffset).r;
        sampled.b = texture(decalTexture, uv - chromaOffset).b;
    }
    sampled.rgb = hueShift(sampled.rgb, effects.x + effects.y * u_Time);
    vec4 themedColor = vec4(uberResolveThemeColor(int(effects.w + 0.5), decalColor.rgb), decalColor.a);
    vec4 decal = sampled * themedColor;
    vec2 maskUv = panUV(transformUV(getUV(_DecalMaskUV, mesh), _DecalMask_ST), _DecalMaskPan, u_Time);
    float faceMask = modifiers.x == 1.0
        ? step(0.0, mesh.isFrontFace)
        : (modifiers.x == 2.0 ? step(mesh.isFrontFace, 0.0) : 1.0);
    float globalMask = uberGlobalMask(int(modifiers.y + 0.5), mesh);
    float depthFade = modifiers.w == 0.0
        ? 1.0
        : smoothstep(-0.5, 2.0, modifiers.w + dot(mesh.worldNormal, mesh.viewDir));
    float localMask = uberChannel(texture(_DecalMask, maskUv), maskChannel);
    float tileCoverage = sourceAlphaParams.w > 0.5 && sourceAlphaParams.z > 0.5 ? 1.0 : bounds;
    decal.a *= localMask * tileCoverage * faceMask * depthFade;
    float maskWeight = localMask;
    if (sourceAlphaParams.w > 0.5 && modifiers.y > 0.5)
    {
        int blendType = int(sourceAlphaParams.w + 0.5) - 1;
        decal.a = uberBlendMask(decal.a, globalMask, blendType);
        maskWeight = uberBlendMask(localMask, globalMask, blendType);
    }
    else
        decal.a *= globalMask;
    alphaControl = vec3(maskWeight, strictBounds, faceMask);
    return decal;
}

void uberBlendDecal(
    inout FragmentData fragData,
    vec4 decal,
    vec4 parameters,
    vec4 sourceAlphaParams,
    vec3 alphaControl)
{
    int blendMode = int(parameters.x + 0.5);
    float opacity = saturate(decal.a * parameters.y);
    vec3 blended = uberBlendRgb(fragData.baseColor, decal.rgb, blendMode);
    fragData.baseColor = mix(fragData.baseColor, blended, opacity);
    if (sourceAlphaParams.w > 0.5)
    {
        int operation = int(sourceAlphaParams.x + 0.5);
        int selector = int(sourceAlphaParams.y + 0.5);
        if (operation != 0 && alphaControl.z > 0.5 &&
            (sourceAlphaParams.z > 0.5 || (selector != 1 && selector != 3) || alphaControl.y > 0.5))
        {
            float finalAlpha = saturate(decal.a * parameters.y);
            float weight = selector == 2 || selector == 3 ? alphaControl.x : 1.0;
            float changedAlpha = fragData.alpha;
            if (operation == 1) changedAlpha = finalAlpha;
            else if (operation == 2) changedAlpha = saturate(fragData.alpha * finalAlpha);
            else if (operation == 3) changedAlpha = saturate(fragData.alpha + finalAlpha);
            else if (operation == 4) changedAlpha = saturate(fragData.alpha - finalAlpha);
            else if (operation == 5) changedAlpha = min(fragData.alpha, finalAlpha);
            else if (operation == 6) changedAlpha = max(fragData.alpha, finalAlpha);
            fragData.alpha = mix(fragData.alpha, changedAlpha, weight);
        }
    }
    else
        fragData.alpha = mix(fragData.alpha, decal.a, saturate(parameters.z) * opacity);
    fragData.emission += decal.rgb * max(parameters.w, 0.0) * opacity;
}

#endif

void uberApplyDecals(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_LAYERED_DECALS
    vec3 alphaControl;
    vec4 decal = uberSampleDecal(_DecalTexture, _DecalColor, _DecalPosition, _DecalScale, _DecalSideOffset, _DecalTexturePan, _DecalRotation, _DecalRotationSpeed, _DecalUvMode0, 0, _DecalSlotModifiers0, _DecalSlotFx0, _DecalAlphaParams0, mesh, alphaControl);
    uberBlendDecal(fragData, decal, _DecalBlendParams0, _DecalAlphaParams0, alphaControl);
    decal = uberSampleDecal(_DecalTexture1, _DecalColor1, _DecalPosition1, _DecalScale1, _DecalSideOffset1, _DecalTexturePan1, _DecalRotation1, _DecalRotationSpeed1, _DecalUvMode1, 1, _DecalSlotModifiers1, _DecalSlotFx1, _DecalAlphaParams1, mesh, alphaControl);
    uberBlendDecal(fragData, decal, _DecalBlendParams1, _DecalAlphaParams1, alphaControl);
    decal = uberSampleDecal(_DecalTexture2, _DecalColor2, _DecalPosition2, _DecalScale2, _DecalSideOffset2, _DecalTexturePan2, _DecalRotation2, _DecalRotationSpeed2, _DecalUvMode2, 2, _DecalSlotModifiers2, _DecalSlotFx2, _DecalAlphaParams2, mesh, alphaControl);
    uberBlendDecal(fragData, decal, _DecalBlendParams2, _DecalAlphaParams2, alphaControl);
    decal = uberSampleDecal(_DecalTexture3, _DecalColor3, _DecalPosition3, _DecalScale3, _DecalSideOffset3, _DecalTexturePan3, _DecalRotation3, _DecalRotationSpeed3, _DecalUvMode3, 3, _DecalSlotModifiers3, _DecalSlotFx3, _DecalAlphaParams3, mesh, alphaControl);
    uberBlendDecal(fragData, decal, _DecalBlendParams3, _DecalAlphaParams3, alphaControl);
#endif
}

#ifndef XRENGINE_UBER_DISABLE_LAYERED_EMISSION
void uberEmissionSlot(
    inout FragmentData fragData,
    sampler2D source,
    sampler2D maskTexture,
    vec4 sourceTransform,
    vec4 maskTransform,
    vec4 color,
    vec4 parameters,
    vec2 pan,
    int uvMode,
    vec2 maskPan,
    int maskUvMode,
    float centerOutSpeed,
    vec4 modifiers,
    vec4 sampling,
    ToonMesh mesh,
    vec3 directColor)
{
    vec2 baseUv = getUV(uvMode, mesh);
    vec2 uv = panUV(transformUV(baseUv, sourceTransform), pan, u_Time);
    bool sourceLayered = _EmissionToonModel > 0.5;
    if (sourceLayered && modifiers.y > 0.5)
    {
        float nDotV = abs(dot(mesh.worldNormal, mesh.viewDir));
        uv = vec2(0.5 + nDotV * 0.5) * sourceTransform.xy +
            vec2(centerOutSpeed * u_Time * 0.05);
    }
    vec2 maskBaseUv = sourceLayered ? getUV(maskUvMode, mesh) : baseUv;
    vec2 maskUv = panUV(transformUV(maskBaseUv, maskTransform), sourceLayered ? maskPan : vec2(0.0), u_Time * 0.05);
    vec4 texel = texture(source, uv);
    vec4 maskTexel = texture(maskTexture, maskUv);
    float mask = maskTexel[int(clamp(sampling.y, 0.0, 3.0) + 0.5)];
    mask = mix(mask, 1.0 - mask, saturate(sampling.z));
    float pulse = mix(1.0, 0.5 + 0.5 * sin(u_Time * parameters.z), saturate(parameters.y));
    vec3 sourceColor = _EmissionToonModel > 0.5
        ? texel.rgb * mix(vec3(1.0), fragData.baseColor, saturate(sampling.x))
        : mix(texel.rgb, fragData.baseColor, saturate(sampling.x));
    vec3 shifted = parameters.w == 0.0 ? sourceColor : hueShift(sourceColor, parameters.w * u_Time);
    vec3 themedColor = uberResolveThemeColor(int(modifiers.w + 0.5), color.rgb);
    float centerOut = !sourceLayered && modifiers.y > 0.5
        ? smoothstep(0.0, 0.5, abs(fract(uv.x + u_Time * parameters.z) - 0.5))
        : 1.0;
    float globalMask = uberGlobalMask(int(modifiers.z + 0.5), mesh);
    float sourceAlpha = _EmissionToonModel > 0.5 ? texel.a : 1.0;
    vec3 emission = shifted * themedColor * max(parameters.x, 0.0) * sourceAlpha * mask * pulse * centerOut * globalMask;
    emission *= xrSourceEmissionFluorescence(directColor, sampling.w);
    fragData.emission += emission;
    fragData.baseColor = mix(fragData.baseColor, emission, saturate(modifiers.x) * mask);
}
#endif

void uberApplyEmissionSlots(inout FragmentData fragData, ToonMesh mesh, vec3 directColor)
{
#ifndef XRENGINE_UBER_DISABLE_LAYERED_EMISSION
    uberEmissionSlot(fragData, _Emission0Tex, _Emission0Mask, _Emission0Tex_ST, _Emission0Mask_ST, _EmissionSlotColor0, _EmissionSlotParams0, _EmissionSlotPan0, _EmissionSlotUv0, _Emission0MaskPan, _Emission0MaskUV, _EmissionSlotCenterOutSpeed0, _EmissionSlotModifiers0, _EmissionSlotSampling0, mesh, directColor);
    uberEmissionSlot(fragData, _Emission1Tex, _Emission1Mask, _Emission1Tex_ST, _Emission1Mask_ST, _EmissionSlotColor1, _EmissionSlotParams1, _EmissionSlotPan1, _EmissionSlotUv1, _Emission1MaskPan, _Emission1MaskUV, _EmissionSlotCenterOutSpeed1, _EmissionSlotModifiers1, _EmissionSlotSampling1, mesh, directColor);
    uberEmissionSlot(fragData, _Emission2Tex, _Emission2Mask, _Emission2Tex_ST, _Emission2Mask_ST, _EmissionSlotColor2, _EmissionSlotParams2, _EmissionSlotPan2, _EmissionSlotUv2, _Emission2MaskPan, _Emission2MaskUV, _EmissionSlotCenterOutSpeed2, _EmissionSlotModifiers2, _EmissionSlotSampling2, mesh, directColor);
    uberEmissionSlot(fragData, _Emission3Tex, _Emission3Mask, _Emission3Tex_ST, _Emission3Mask_ST, _EmissionSlotColor3, _EmissionSlotParams3, _EmissionSlotPan3, _EmissionSlotUv3, _Emission3MaskPan, _Emission3MaskUV, _EmissionSlotCenterOutSpeed3, _EmissionSlotModifiers3, _EmissionSlotSampling3, mesh, directColor);
#endif
}

#ifndef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
#ifndef XRENGINE_UBER_DISABLE_AUDIOLINK
float uberAudioBandValue(int band, float history);
#endif

// Projection modes: 0 view normal, 1 top pinch, 2 reflection, 3 view gradient, 4 light half vector.
vec2 uberMatcapUv(ToonMesh mesh, ToonLight light, vec4 projection, vec3 projectedNormal)
{
    vec3 normal = projectedNormal;
    if (_MatcapToonModel <= 0.5) normal = normalize(normal);
    int uvMode = int(projection.z + 0.5);
    float border = max(projection.y, 0.0);

    if (uvMode == 1)
    {
        vec3 viewUp = normalize(vec3(0.0, 1.0, 0.0) - mesh.viewDir * dot(mesh.viewDir, vec3(0.0, 1.0, 0.0)));
        vec3 viewRight = normalize(cross(mesh.viewDir, viewUp));
        // The source Z reflection reverses this cross-product axis.
        if (_MatcapToonModel > 0.5) viewRight = -viewRight;
        return vec2(dot(viewRight, normal), dot(viewUp, normal)) * border + 0.5;
    }

    if (uvMode == 2)
    {
        vec3 reflected = reflect(-mesh.viewDir, normal);
        return reflected.xy * border + 0.5;
    }

    if (uvMode == 3)
        return vec2(1.0 - abs(dot(normal, mesh.viewDir)));

    if (uvMode == 4)
    {
        vec3 tangent = cross(normal, vec3(0.0, 1.0, 0.0));
        // A vertical normal has no cross product with the up axis. Use the forward axis for it.
        // The source forward axis is the engine -Z axis.
        if (dot(tangent, tangent) < 1e-12)
            tangent = cross(normal, vec3(0.0, 0.0, _MatcapToonModel > 0.5 ? -1.0 : 1.0));
        tangent = normalize(tangent);
        // The source Z reflection reverses this cross-product axis.
        if (_MatcapToonModel > 0.5)
            tangent = -tangent;
        vec3 bitangent = normalize(cross(normal, tangent));
        if (_MatcapToonModel > 0.5)
            bitangent = -bitangent;
        return vec2(dot(light.halfDir, tangent), dot(light.halfDir, bitangent)) * border + 0.5;
    }

    if (_MatcapToonModel > 0.5)
    {
        vec3 detail = (mat3(u_ViewMatrix) * normal) * vec3(-1.0, -1.0, 1.0);
        vec3 base = (mat3(u_ViewMatrix) * mesh.viewDir) * vec3(-1.0, -1.0, 1.0) + vec3(0.0, 0.0, 1.0);
        vec3 corrected = base * (dot(base, detail) / base.z) - detail;
        return corrected.xy * border + 0.5;
    }
    vec3 viewNormal = normalize(mat3(u_ViewMatrix) * normal);
    return viewNormal.xy * border + 0.5;
}

// Applies the matcap shape, alpha, and hue options of one slot.
// shape: x is not used (the rotation argument rotates the UV), y circle mask toggle, z circle radius, w circle blur.
// alphaOptions: x alpha override, y intensity-to-alpha blend weight (0 or less is off),
// z intensity source (0 grayscale, 1 max channel), w blend type (0 add, 1 multiply).
// hueSelect: 1 replaces the hue with the shift value, 0 offsets the hue.
vec4 uberMatcapSlot(
    sampler2D source,
    sampler2D maskTexture,
    vec4 color,
    int themeIndex,
    vec4 parameters,
    vec4 projection,
    vec4 sourceOptions,
    vec4 maskOptions,
    vec4 uvBlend,
    vec4 uvControl,
    float rotation,
    vec4 hue,
    vec4 audioBands,
    vec2 audioAlpha,
    vec2 audioEmission,
    vec2 audioIntensity,
    vec4 shape,
    vec4 alphaOptions,
    int hueSelect,
    vec4 sourceSt,
    vec2 sourcePan,
    vec4 maskSt,
    vec2 maskPan,
    ToonMesh mesh,
    ToonLight light,
    vec3 baseColor,
    inout float alpha,
    inout vec3 emission,
    inout vec3 lightAdd)
{
    vec3 projectedNormal = mix(mesh.vertexNormal, mesh.worldNormal, saturate(parameters.w));
    if (uvBlend.z > 0.5)
    {
        vec2 normalUv = panUV(transformUV(getUV(0, mesh), _Matcap1NormalMap_ST),
            _Matcap1NormalMapPan, u_Time * 0.05);
        vec3 mappedNormal = unpackNormal(texture(_Matcap1NormalMap, normalUv), uvBlend.w);
        projectedNormal = normalize(mesh.TBN[0] * mappedNormal.x +
            mesh.TBN[1] * mappedNormal.y + mesh.worldNormal * mappedNormal.z);
    }
    vec2 uv = uberMatcapUv(mesh, light, projection, projectedNormal);
    bool sourceMatcap = _MatcapToonModel > 0.5;
    vec4 maskSample = vec4(1.0);
    // The circle mask uses the projected UV before pan, rotation, and transform.
    vec2 circleUv = uv;
    if (sourceMatcap)
    {
        vec2 maskUv = panUV(transformUV(getUV(int(maskOptions.w + 0.5), mesh), maskSt), maskPan, u_Time * 0.05);
        maskSample = texture(maskTexture, maskUv);
        if (uvBlend.x != 0.0 || uvBlend.y != 0.0)
            uv = mix(uv, getUV(int(uvControl.x + 0.5), mesh), uvBlend.xy);
        circleUv = uv;
        uv = panUV(uv, sourcePan, u_Time * 0.05);
        float angle = rotation * 3.14159265359;
        float sine = sin(angle), cosine = cos(angle);
        vec2 centeredUv = uv - 0.5;
        uv = vec2(centeredUv.x * cosine + centeredUv.y * sine,
            -centeredUv.x * sine + centeredUv.y * cosine) + 0.5;
        uv = transformUV(uv, sourceSt);
        // Convert procedural source UVs to the imported texture origin.
        uv.y = 1.0 - uv.y;
    }
    float smoothness = clamp(sourceOptions.x, 0.0, 1.0);
    if (sourceMatcap && sourceOptions.w > 0.5)
        smoothness *= maskSample[clamp(int(maskOptions.z + 0.5), 0, 3)];
    float lod = (1.0 - smoothness) * floor(log2(float(max(textureSize(source, 0).x, textureSize(source, 0).y))));
    vec4 sampleColor = sourceMatcap && uvControl.y > 0.5
        ? textureLod(source, uv, lod) : texture(source, uv);
    sampleColor.rgb *= uberResolveThemeColor(themeIndex, color.rgb);
    sampleColor.a *= color.a;
    float intensity = parameters.x;
    float emissionStrength = parameters.z;
#ifndef XRENGINE_UBER_DISABLE_AUDIOLINK
    if (sourceMatcap && audioBands.x > 0.5 && _AudioLinkAnimToggle != 0)
    {
        float alphaBand = uberAudioBandValue(int(audioBands.y), 0.0);
        float emissionBand = uberAudioBandValue(int(audioBands.z), 0.0);
        float intensityBand = uberAudioBandValue(int(audioBands.w), 0.0);
        sampleColor.a = clamp(sampleColor.a + mix(audioAlpha.x, audioAlpha.y, alphaBand), 0.0, 1.0);
        emissionStrength += mix(audioEmission.x, audioEmission.y, emissionBand);
        intensity = max(0.0, intensity + mix(audioIntensity.x, audioIntensity.y, intensityBand));
    }
#endif
    float mask = sourceMatcap ? maskSample[clamp(int(maskOptions.x + 0.5), 0, 3)] : texture(maskTexture, getUV(0, mesh)).r;
    if (sourceMatcap && maskOptions.y > 0.5) mask = 1.0 - mask;
    if (sourceMatcap && shape.y > 0.5)
    {
        float circleDistance = distance(vec2(0.5), circleUv);
        float circleWidth = fwidth(circleDistance);
        float circleMin = saturate(shape.z - shape.w * 0.5);
        float circleMax = saturate(shape.z + shape.w * 0.5);
        mask *= 1.0 - smoothstep(circleMin - circleWidth, circleMax + circleWidth, circleDistance);
    }
    sampleColor.rgb *= mix(vec3(1.0), light.color, saturate(projection.w));
    sampleColor.rgb *= max(intensity, 0.0);
    if (sourceMatcap)
    {
        sampleColor.rgb = mix(sampleColor.rgb, sampleColor.rgb * baseColor, clamp(sourceOptions.y, 0.0, 1.0));
        if (hue.x > 0.5)
        {
            float shift = hue.y + hue.z * u_Time * 0.05;
            sampleColor.rgb = uberSelectOrShiftHue(sampleColor.rgb, shift,
                int(hue.w + 0.5), hueSelect > 0 ? 0 : 1);
        }

        // Alpha options use the slot mask before the light and global masks.
        alpha *= mix(1.0, sampleColor.a, mask * alphaOptions.x);
        if (alphaOptions.y > 0.0)
        {
            float intensity = alphaOptions.z > 0.5
                ? max(max(sampleColor.r, sampleColor.g), sampleColor.b)
                : dot(sampleColor.rgb, vec3(0.299, 0.587, 0.114));
            alpha = alphaOptions.w > 0.5
                ? alpha * mix(1.0, intensity, alphaOptions.y)
                : saturate(alpha + intensity * alphaOptions.y);
        }
    }
    mask *= mix(1.0, saturate(light.lightMap), saturate(parameters.y));
#ifndef XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES
    if (sourceMatcap && uvControl.z > 0.5)
        mask = uberBlendMask(mask, uberGlobalMask(int(uvControl.z + 0.5), mesh, 0.05),
            int(uvControl.w + 0.5));
#endif
    mask *= sampleColor.a;
    if (sourceMatcap) lightAdd += sampleColor.rgb * sourceOptions.z * mask;
    emission += sampleColor.rgb * max(emissionStrength, 0.0) * mask;
    return vec4(sampleColor.rgb, mask);
}

void uberBlendMatcapSlot(inout vec3 finalColor, vec4 slot, vec4 blend, float mixed)
{
    float mask = saturate(slot.a);
    finalColor = mix(finalColor, slot.rgb, saturate(blend.x) * mask * 0.999999);
    finalColor *= mix(vec3(1.0), slot.rgb, saturate(blend.y) * mask);
    finalColor += slot.rgb * max(blend.z, 0.0) * mask;
    finalColor = mix(finalColor, uberBlendRgb(finalColor, slot.rgb, 6), saturate(blend.w) * mask);
    finalColor = mix(finalColor, finalColor + finalColor * slot.rgb, saturate(mixed) * mask);
}
#endif

void uberApplyMatcapSlots(inout vec3 finalColor, inout float alpha, inout vec3 emission, inout vec3 lightAdd, ToonMesh mesh, ToonLight light)
{
#ifndef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
    bool audioActive = false;
#ifndef XRENGINE_UBER_DISABLE_AUDIOLINK
    audioActive = _AudioLinkAnimToggle != 0;
#endif
    if (_MatcapSlotParams0.x > 0.0 || (audioActive && _MatcapSlotAudioBands0.x > 0.5 &&
        (max(_MatcapSlotAudioIntensity0.x, _MatcapSlotAudioIntensity0.y) > 0.0 ||
         max(_MatcapSlotAudioEmission0.x, _MatcapSlotAudioEmission0.y) > 0.0)))
    {
        vec4 value = uberMatcapSlot(
            _Matcap0Tex, _Matcap0Mask, _MatcapSlotColor0, _MatcapSlotTheme0,
            _MatcapSlotParams0, _MatcapSlotExtra0, _MatcapSlotSource0, _MatcapSlotMask0,
            _MatcapSlotUvBlend0, _MatcapSlotUvControl0, _MatcapSlotRotation0, _MatcapSlotHue0,
            _MatcapSlotAudioBands0, _MatcapSlotAudioAlpha0, _MatcapSlotAudioEmission0, _MatcapSlotAudioIntensity0,
            _MatcapSlotShape0, _MatcapSlotAlpha0, _MatcapSlotHueSelect0,
            _Matcap0Tex_ST, _Matcap0TexPan, _Matcap0Mask_ST, _Matcap0MaskPan,
            mesh, light, finalColor, alpha, emission, lightAdd);
        uberBlendMatcapSlot(finalColor, value, _MatcapSlotBlend0, _MatcapSlotExtra0.x);
    }
    if (_MatcapSlotParams1.x > 0.0 || (audioActive && _MatcapSlotAudioBands1.x > 0.5 &&
        (max(_MatcapSlotAudioIntensity1.x, _MatcapSlotAudioIntensity1.y) > 0.0 ||
         max(_MatcapSlotAudioEmission1.x, _MatcapSlotAudioEmission1.y) > 0.0)))
    {
        vec4 value = uberMatcapSlot(
            _Matcap1Tex, _Matcap1Mask, _MatcapSlotColor1, _MatcapSlotTheme1,
            _MatcapSlotParams1, _MatcapSlotExtra1, _MatcapSlotSource1, _MatcapSlotMask1,
            _MatcapSlotUvBlend1, _MatcapSlotUvControl1, _MatcapSlotRotation1, _MatcapSlotHue1,
            _MatcapSlotAudioBands1, _MatcapSlotAudioAlpha1, _MatcapSlotAudioEmission1, _MatcapSlotAudioIntensity1,
            _MatcapSlotShape1, _MatcapSlotAlpha1, _MatcapSlotHueSelect1,
            _Matcap1Tex_ST, _Matcap1TexPan, _Matcap1Mask_ST, _Matcap1MaskPan,
            mesh, light, finalColor, alpha, emission, lightAdd);
        uberBlendMatcapSlot(finalColor, value, _MatcapSlotBlend1, _MatcapSlotExtra1.x);
    }
    if (_MatcapSlotParams2.x > 0.0 || (audioActive && _MatcapSlotAudioBands2.x > 0.5 &&
        (max(_MatcapSlotAudioIntensity2.x, _MatcapSlotAudioIntensity2.y) > 0.0 ||
         max(_MatcapSlotAudioEmission2.x, _MatcapSlotAudioEmission2.y) > 0.0)))
    {
        vec4 value = uberMatcapSlot(
            _Matcap2Tex, _Matcap2Mask, _MatcapSlotColor2, _MatcapSlotTheme2,
            _MatcapSlotParams2, _MatcapSlotExtra2, _MatcapSlotSource2, _MatcapSlotMask2,
            _MatcapSlotUvBlend2, _MatcapSlotUvControl2, _MatcapSlotRotation2, _MatcapSlotHue2,
            _MatcapSlotAudioBands2, _MatcapSlotAudioAlpha2, _MatcapSlotAudioEmission2, _MatcapSlotAudioIntensity2,
            _MatcapSlotShape2, _MatcapSlotAlpha2, _MatcapSlotHueSelect2,
            _Matcap2Tex_ST, _Matcap2TexPan, _Matcap2Mask_ST, _Matcap2MaskPan,
            mesh, light, finalColor, alpha, emission, lightAdd);
        uberBlendMatcapSlot(finalColor, value, _MatcapSlotBlend2, _MatcapSlotExtra2.x);
    }
    if (_MatcapSlotParams3.x > 0.0 || (audioActive && _MatcapSlotAudioBands3.x > 0.5 &&
        (max(_MatcapSlotAudioIntensity3.x, _MatcapSlotAudioIntensity3.y) > 0.0 ||
         max(_MatcapSlotAudioEmission3.x, _MatcapSlotAudioEmission3.y) > 0.0)))
    {
        vec4 value = uberMatcapSlot(
            _Matcap3Tex, _Matcap3Mask, _MatcapSlotColor3, _MatcapSlotTheme3,
            _MatcapSlotParams3, _MatcapSlotExtra3, _MatcapSlotSource3, _MatcapSlotMask3,
            _MatcapSlotUvBlend3, _MatcapSlotUvControl3, _MatcapSlotRotation3, _MatcapSlotHue3,
            _MatcapSlotAudioBands3, _MatcapSlotAudioAlpha3, _MatcapSlotAudioEmission3, _MatcapSlotAudioIntensity3,
            _MatcapSlotShape3, _MatcapSlotAlpha3, _MatcapSlotHueSelect3,
            _Matcap3Tex_ST, _Matcap3TexPan, _Matcap3Mask_ST, _Matcap3MaskPan,
            mesh, light, finalColor, alpha, emission, lightAdd);
        uberBlendMatcapSlot(finalColor, value, _MatcapSlotBlend3, _MatcapSlotExtra3.x);
    }
#endif
}

vec3 uberApplySecondRim(ToonMesh mesh, ToonLight light)
{
#ifdef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
    return vec3(0.0);
#else
    if (_Rim2Settings.x > 0.5)
        return vec3(0.0);
    float fresnel = pow(1.0 - saturate(dot(mesh.worldNormal, mesh.viewDir)),
        max(_Rim2Params.y, EPSILON));
    float rim = smoothstep(1.0 - _Rim2Params.x, 1.0, fresnel);
    rim *= texture(_Rim2Mask, transformUV(mesh.uv[0], _Rim2Mask_ST)).r;
    rim *= mix(1.0, saturate(light.lightMap), saturate(_Rim2Params.z));
    return _Rim2Color.rgb * rim * max(_Rim2Params.w, 0.0);
#endif
}

vec3 uberApplyDepthRim(ToonMesh mesh)
{
#if defined(XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM) || defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING) || defined(XRENGINE_UBER_DISABLE_FORWARD_SHADOWS) || defined(XRENGINE_UBER_DISABLE_FORWARD_CONTACT_SHADOWS)
    return vec3(0.0);
#else
    if (_DepthRimStrength <= EPSILON || !ForwardContactShadowsEnabled)
        return vec3(0.0);

    vec2 pixel = gl_FragCoord.xy - ScreenOrigin;
    vec2 size = vec2(ScreenWidth, ScreenHeight);
    vec2 uv = pixel / size;
    vec2 offset = vec2(max(_DepthRimWidth, 1.0) / size.x, 0.0);
    float centerDepth;
    float neighborDepth;
    if (ForwardContactShadowsArrayEnabled)
    {
        float layer = float(XRENGINE_GetForwardResolvedViewIndex());
        centerDepth = texture(ForwardContactDepthViewArray, vec3(uv, layer)).r;
        neighborDepth = texture(ForwardContactDepthViewArray, vec3(uv + offset, layer)).r;
    }
    else
    {
        centerDepth = texture(ForwardContactDepthView, uv).r;
        neighborDepth = texture(ForwardContactDepthView, uv + offset).r;
    }
    float edge = smoothstep(0.00001, 0.0025, abs(centerDepth - neighborDepth));
    return _DepthRimColor.rgb * edge * _DepthRimStrength;
#endif
}

float uberApplyDepthIntersectionAlpha(float alpha, ToonMesh mesh)
{
#if defined(XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS) || defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING) || defined(XRENGINE_UBER_DISABLE_FORWARD_SHADOWS) || defined(XRENGINE_UBER_DISABLE_FORWARD_CONTACT_SHADOWS)
    return alpha;
#else
    if (_DepthAlphaEnabled <= 0.5 || !ForwardContactShadowsEnabled)
        return alpha;

    vec2 pixel = gl_FragCoord.xy - ScreenOrigin;
    vec2 uv = pixel / vec2(ScreenWidth, ScreenHeight);
    float sceneDepth;
    if (ForwardContactShadowsArrayEnabled)
    {
        float layer = float(XRENGINE_GetForwardResolvedViewIndex());
        sceneDepth = texture(ForwardContactDepthViewArray, vec3(uv, layer)).r;
    }
    else
    {
        sceneDepth = texture(ForwardContactDepthView, uv).r;
    }

    if (XRENGINE_IsContactShadowFarDepth(sceneDepth, DepthMode))
        return alpha;

    vec3 sceneWorldPosition = XRENGINE_ContactShadowWorldPosFromDepth(
        sceneDepth,
        uv,
        XRENGINE_GetForwardResolvedInverseProjMatrix(),
        XRENGINE_GetForwardResolvedInverseViewMatrix(),
        DepthMode);
    float separation = distance(sceneWorldPosition, mesh.worldPos);
    float depthRange = max(_DepthAlphaParams.w - _DepthAlphaParams.z, EPSILON);
    float depthBlend = saturate((separation - _DepthAlphaParams.z) / depthRange);
    float authoredAlpha = mix(_DepthAlphaParams.x, _DepthAlphaParams.y, depthBlend);
    return alpha * saturate(authoredAlpha);
#endif
}

#ifndef XRENGINE_UBER_DISABLE_ADVANCED_PBR
vec3 uberApplyCubemap(
    ToonMesh mesh,
    ToonLight light,
    bool sourceComposition,
    inout vec3 color,
    inout vec3 emission)
{
    vec3 reflected = reflect(-mesh.viewDir, mesh.worldNormal);
    vec3 cubeDirection = reflected;
    if (_CubeMapUvMode == 0)
        cubeDirection = -mesh.viewDir;
    else if (_CubeMapUvMode == 2 || _CubeMapUvMode == 3)
        cubeDirection = mesh.worldNormal;
    cubeDirection.z *= _CubeMapCoordinateZSign;

    float cubeLod = (1.0 - saturate(_CubeMapSmoothness));
    cubeLod = cubeLod * cubeLod * 8.0;
    vec4 cubeSample = textureLod(_CubeMap, cubeDirection, cubeLod);
    vec3 environment = cubeSample.rgb * _CubeMapColor.rgb * max(_CubeMapStrength, 0.0);
    vec4 cubeMaskTexel = texture(_CubeMapMask, transformUV(mesh.uv[0], _CubeMapMask_ST));
    float cubeMask = cubeMaskTexel[clamp(_CubeMapMaskChannel, 0, 3)];
    if (sourceComposition && _CubeMapMaskGlobalMask > 0)
        cubeMask = uberBlendMask(cubeMask,
            uberGlobalMask(_CubeMapMaskGlobalMask, mesh, 0.05),
            _CubeMapMaskGlobalMaskBlendType);
    cubeMask = mix(cubeMask, 1.0 - cubeMask, saturate(_CubeMapMaskInvert));
    float lightMask = mix(1.0, saturate(light.lightMap), saturate(_CubeMapLightMask));
    cubeMask = sourceComposition ? min(cubeMask, lightMask) : cubeMask * lightMask;
    float cubeAlpha = saturate(cubeMask * cubeSample.a * _CubeMapBlendAmount);
    if (_CubeMapBlendType == 1)
        color *= mix(vec3(1.0), environment, cubeAlpha);
    else if (_CubeMapBlendType == 2)
        color += environment * cubeAlpha;
    else
        color = mix(color, environment, cubeAlpha);
    emission += environment * max(_CubeMapEmissionStrength, 0.0) * cubeMask * cubeSample.a;
    return environment;
}
#endif

vec3 uberApplyAdvancedPbr(
    ToonMesh mesh,
    ToonLight light,
    PBRData pbr,
    vec3 finalColor,
    inout vec3 emission)
{
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_PBR
    return finalColor;
#else
    vec3 tangent = normalize(mesh.TBN[0]);
    vec3 bitangent = normalize(mesh.TBN[1]);
    float rotation = _AnisotropyRotation * TWO_PI;
    vec3 anisoTangent = tangent * cos(rotation) + bitangent * sin(rotation);
    vec3 anisoHalf = normalize(light.halfDir + anisoTangent * _Anisotropy);
    float anisoSpec = pow(saturate(dot(mesh.worldNormal, anisoHalf)),
        mix(8.0, 256.0, saturate(_SpecularSmoothness)));

    float spec2 = pow(saturate(light.nDotH),
        mix(4.0, 512.0, saturate(_Specular2Smoothness))) * _Specular2Strength;
    finalColor += (_Specular2Color.rgb * spec2 + pbr.specularColor * anisoSpec * abs(_Anisotropy)) * light.color;

    float coatExponent = mix(16.0, 1024.0, saturate(_ClearCoatSmoothness));
    float coat = pow(saturate(light.nDotH), coatExponent) * saturate(light.nDotL) * _ClearCoat;
    finalColor += vec3(coat);

    vec3 environment = vec3(0.0);
    if (_CubeMapToonModel == 0 && _EnviroRimSettings.x < 0.5)
        environment = uberApplyCubemap(mesh, light, false, finalColor, emission);
    else if (_EnviroRimSettings.x < 0.5 && _RimEnviroIntensity > 0.0)
    {
        vec3 direction = reflect(-mesh.viewDir, mesh.worldNormal);
        if (_CubeMapUvMode == 0) direction = -mesh.viewDir;
        else if (_CubeMapUvMode == 2 || _CubeMapUvMode == 3) direction = mesh.worldNormal;
        direction.z *= _CubeMapCoordinateZSign;
        float roughness = 1.0 - saturate(_CubeMapSmoothness);
        environment = textureLod(_CubeMap, direction, roughness * roughness * 8.0).rgb *
            _CubeMapColor.rgb * max(_CubeMapStrength, 0.0);
    }

    float rim = pow(1.0 - saturate(dot(mesh.worldNormal, mesh.viewDir)),
        mix(0.25, 16.0, saturate(_RimEnviroSharpness)));
    rim = smoothstep(1.0 - _RimEnviroWidth, 1.0, rim);
    if (_EnviroRimSettings.x < 0.5)
        finalColor += environment * rim * _RimEnviroIntensity;

    float backlight = saturate(dot(-mesh.worldNormal, light.direction));
    float backMask = texture(_BacklightMask, mesh.uv[0]).r;
    finalColor += _BacklightColor.rgb * backlight * backMask * _BacklightStrength;
    return finalColor;
#endif
}

vec3 uberApplySourceEnviroRim(ToonMesh mesh, vec3 baseColor,
    vec3 probeRadiance)
{
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_PBR
    return vec3(0.0);
#else
    if (_EnviroRimSettings.x < 0.5 || _RimEnviroIntensity <= 0.0)
        return vec3(0.0);
    float width = _RimEnviroWidth;
    float edge = saturate(1.0 - smoothstep(min(_RimEnviroSharpness, width),
        width, abs(dot(mesh.worldNormal, mesh.viewDir))));
    vec2 uv = panUV(transformUV(getUV(_RimEnviroMaskUV, mesh),
        _RimEnviroMask_ST), _RimEnviroMaskPan, u_Time);
    float mask = texture(_RimEnviroMask, uv)[clamp(int(_EnviroRimSettings.w + 0.5), 0, 3)];
    return max((probeRadiance - vec3(_EnviroRimSettings.z)) * baseColor,
        vec3(0.0)) * edge * mask * _RimEnviroIntensity;
#endif
}

#ifndef XRENGINE_UBER_DISABLE_TEXTURE_ARRAY_FLIPBOOK
#define XR_FLIP_F(name) name
#define XR_FLIP_I(name) name
#define XR_FLIP_V(name) name
#define XR_FLIP_V2(name) name
#define XR_FLIP_SAMPLE(uv, layer, dx, dy) textureGrad(_FlipbookTexArray, vec3(uv, layer), dx, dy)
#define XR_FLIP_BLEND(base, layer, mode) uberBlendRgb(base, layer, mode)
#define XR_FLIP_RGB_TO_HSV(value) rgbToHsv(value)
#define XR_FLIP_HSV_TO_RGB(value) hsvToRgb(value)
#define XR_FLIP_RGB_TO_OKLAB(value) rgbToOklab(value)
#define XR_FLIP_OKLAB_TO_RGB(value) oklabToRgb(value)
#include "Uber/source_flipbook.glsl"
#undef XR_FLIP_F
#undef XR_FLIP_I
#undef XR_FLIP_V
#undef XR_FLIP_V2
#undef XR_FLIP_SAMPLE
#undef XR_FLIP_BLEND
#undef XR_FLIP_RGB_TO_HSV
#undef XR_FLIP_HSV_TO_RGB
#undef XR_FLIP_RGB_TO_OKLAB
#undef XR_FLIP_OKLAB_TO_RGB
#endif

void uberApplySourceFlipbook(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_TEXTURE_ARRAY_FLIPBOOK
    if (_FlipbookToonModel == 0) return;
#if defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS) || defined(XRENGINE_OUTLINE_PASS)
    const bool shadowPass = true;
    if (_FlipbookAlphaControlsFinalAlpha != 1) return;
#else
    const bool shadowPass = false;
#endif
    vec2 uv = getUV(_FlipbookTexArrayUV, mesh);
    vec2 maskUv = getUV(_FlipbookMaskUV, mesh) * _FlipbookMask_ST.xy +
        _FlipbookMask_ST.zw + _FlipbookMaskPan * u_Time * 0.05;
    vec4 mask = texture(_FlipbookMask, maskUv);
    vec4 base = vec4(fragData.baseColor, fragData.alpha);
    xrApplySourceFlipbook(uv, dFdx(uv), dFdy(uv), vec3(textureSize(_FlipbookTexArray, 0)),
        mask[clamp(int(_FlipbookFlags.z), 0, 3)], u_Time, shadowPass, base, fragData.emission);
    fragData.baseColor = base.rgb;
    fragData.alpha = base.a;
#endif
}

void uberApplyFlipbookArray(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_TEXTURE_ARRAY_FLIPBOOK
    if (_FlipbookToonModel != 0) return;
    int layerCount = max(textureSize(_FlipbookTexArray, 0).z, 1);
    float firstFrame = clamp(_FlipbookStartFrame, 0.0, float(layerCount - 1));
    float lastFrame = _FlipbookEndFrame > firstFrame
        ? min(_FlipbookEndFrame, float(layerCount - 1))
        : float(layerCount - 1);
    float frameCount = max(lastFrame - firstFrame + 1.0, 1.0);
    float timeline = _FlipbookManualFrameControl > 0.5
        ? _FlipbookCurrentFrame
        : u_Time * _FlipbookFPS + _FlipbookFrameOffset;
    float frame = firstFrame + mod(max(timeline, 0.0), frameCount);
    float frame0 = floor(frame);
    float frame1 = firstFrame + mod(frame0 - firstFrame + 1.0, frameCount);
    float phase = fract(frame);
    float crossfade = _FlipbookCrossfadeEnabled > 0.5
        ? smoothstep(_FlipbookCrossfadeRange.x, max(_FlipbookCrossfadeRange.y, _FlipbookCrossfadeRange.x + EPSILON), phase)
        : 0.0;

    vec2 uv = getUV(_FlipbookTexArrayUV, mesh);
    uv = transformUV(uv, _FlipbookScaleOffset);
    uv = panUV(uv, _FlipbookTexArrayPan, u_Time);
    vec4 flipbook = mix(
        texture(_FlipbookTexArray, vec3(uv, frame0)),
        texture(_FlipbookTexArray, vec3(uv, frame1)),
        crossfade);
    flipbook *= _FlipbookColor;
    if (_FlipbookHueShiftEnabled > 0.5)
        flipbook.rgb = hueShift(flipbook.rgb, _FlipbookHueShift + _FlipbookHueShiftSpeed * u_Time);

    vec4 maskSample = texture(_FlipbookMask, uv);
    flipbook.a *= maskSample.r;
    vec3 blended = uberBlendRgb(fragData.finalColor, flipbook.rgb, _FlipbookBlendType);
    fragData.finalColor = mix(fragData.finalColor, blended, saturate(flipbook.a * _FlipbookReplace));
    fragData.emission += flipbook.rgb * flipbook.a * _FlipbookEmissionStrength;
    if (_FlipbookAlphaControlsFinalAlpha == 1)
        fragData.alpha = flipbook.a;
    else if (_FlipbookAlphaControlsFinalAlpha == 2)
    {
        fragData.baseColor = flipbook.rgb;
        fragData.alpha = flipbook.a;
    }
#endif
}

#endif
