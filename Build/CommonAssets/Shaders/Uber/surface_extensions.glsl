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

vec4 uberSampleStochastic(sampler2D source, vec2 uv, int mode)
{
    if (mode == 0)
        return texture(source, uv);

    vec2 dx = dFdx(uv);
    vec2 dy = dFdy(uv);
    vec2 cell = floor(uv * 64.0);

    if (mode == 1)
    {
        // Deliot-Heitz style decorrelation: three triangular-lattice samples
        // are reoriented per cell while retaining the original derivatives.
        float seed = uberHash21(cell);
        vec2 o0 = vec2(seed, fract(seed * 7.13));
        vec2 o1 = vec2(fract(seed * 13.7), fract(seed * 3.71));
        vec2 o2 = vec2(fract(seed * 5.31), fract(seed * 11.9));
        vec4 a = textureGrad(source, uv + o0, dx, dy);
        vec4 b = textureGrad(source, uv + o1, dx, dy);
        vec4 c = textureGrad(source, uv + o2, dx, dy);
        vec3 weights = vec3(0.5, 0.3, 0.2);
        return a * weights.x + b * weights.y + c * weights.z;
    }

    // Hex-tile sampling uses three neighboring skewed cells. The blend is
    // continuous across cell boundaries and textureGrad keeps mip selection
    // stable under animated projections.
    const mat2 skew = mat2(1.0, 0.0, 0.5, 0.8660254);
    vec2 hex = skew * uv;
    vec2 baseCell = floor(hex);
    vec2 local = fract(hex);
    vec2 cellA = baseCell;
    vec2 cellB = baseCell + vec2(local.x > local.y ? 1.0 : 0.0, local.x > local.y ? 0.0 : 1.0);
    vec2 cellC = baseCell + vec2(1.0);
    vec2 offsetA = vec2(uberHash21(cellA), uberHash21(cellA + 17.0));
    vec2 offsetB = vec2(uberHash21(cellB), uberHash21(cellB + 17.0));
    vec2 offsetC = vec2(uberHash21(cellC), uberHash21(cellC + 17.0));
    vec3 weights = max(vec3(1.0 - local.x, abs(local.x - local.y), local.y), vec3(0.001));
    weights /= dot(weights, vec3(1.0));
    return textureGrad(source, uv + offsetA, dx, dy) * weights.x
         + textureGrad(source, uv + offsetB, dx, dy) * weights.y
         + textureGrad(source, uv + offsetC, dx, dy) * weights.z;
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
#endif
    return panUV(transformUV(uv, _MainTex_ST), _MainTexPan, u_Time);
}

vec4 uberSampleMainTexture(ToonMesh mesh)
{
    vec2 uv = uberMainUv(mesh);
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return texture(_MainTex, uv);
#else
    return uberSampleStochastic(_MainTex, uv, _MainTexStochasticMode);
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

vec4 uberSampleGlobalMaskTexture(int textureIndex, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES
    return vec4(1.0);
#else
    vec4 value;
    if (textureIndex == 0)
        value = texture(_GlobalMaskTexture0, panUV(transformUV(getUV(_GlobalMaskTexture0UV, mesh), _GlobalMaskTexture0_ST), _GlobalMaskTexture0Pan, u_Time));
    else if (textureIndex == 1)
        value = texture(_GlobalMaskTexture1, panUV(transformUV(getUV(_GlobalMaskTexture1UV, mesh), _GlobalMaskTexture1_ST), _GlobalMaskTexture1Pan, u_Time));
    else if (textureIndex == 2)
        value = texture(_GlobalMaskTexture2, panUV(transformUV(getUV(_GlobalMaskTexture2UV, mesh), _GlobalMaskTexture2_ST), _GlobalMaskTexture2Pan, u_Time));
    else
        value = texture(_GlobalMaskTexture3, panUV(transformUV(getUV(_GlobalMaskTexture3UV, mesh), _GlobalMaskTexture3_ST), _GlobalMaskTexture3Pan, u_Time));

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

float uberGlobalMask(int encodedIndex, ToonMesh mesh)
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
    vec4 mask = uberSampleGlobalMaskTexture(zeroBased / 4, mesh);
    if (channel == 0)
        return mask.r;
    if (channel == 1)
        return mask.g;
    if (channel == 2)
        return mask.b;
    return mask.a;
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

float uberInterleavedGradientNoise(vec2 pixel, float frame)
{
    return fract(52.9829189 * fract(dot(pixel + frame, vec2(0.06711056, 0.00583715))));
}

float uberApplyExtendedAlpha(float alpha, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return alpha;
#else
    float distanceToCamera = length(mesh.worldPos - CameraPosition);
    float distanceFade = 1.0 - smoothstep(
        _DistanceFadeMin,
        max(_DistanceFadeMax, _DistanceFadeMin + EPSILON),
        distanceToCamera);
    alpha *= mix(1.0, distanceFade, saturate(_DistanceFade));

    if (_AlphaFresnel > 0.5)
    {
        float fresnel = pow(1.0 - saturate(dot(mesh.worldNormal, mesh.viewDir)),
            mix(0.25, 16.0, saturate(_AlphaFresnelSharpness)));
        fresnel = smoothstep(1.0 - _AlphaFresnelWidth, 1.0, fresnel);
        fresnel = mix(fresnel, 1.0 - fresnel, saturate(_AlphaFresnelInvert));
        alpha = mix(alpha, alpha * fresnel, saturate(_AlphaFresnelAlpha));
    }

    if (_AlphaAngular > 0.5)
    {
        vec3 forward = normalize(_AngleForwardDirection);
        float angle = degrees(acos(clamp(abs(dot(forward, mesh.viewDir)), 0.0, 1.0)));
        float angular = smoothstep(_CameraAngleMin, max(_CameraAngleMax, _CameraAngleMin + EPSILON), angle);
        alpha *= mix(_AngleMinAlpha, 1.0, angular);
    }

    if (_AlphaDither > 0.0)
    {
        float screenNoise = uberInterleavedGradientNoise(gl_FragCoord.xy, floor(u_Time * _AlphaDitherSpeed));
        float objectNoise = uberHash21(floor(mesh.localPos.xy * 64.0));
        float threshold = mix(screenNoise, objectNoise, saturate(_AlphaDitherGradient));
        alpha = alpha >= mix(1.0, threshold, saturate(_AlphaDither)) ? alpha : 0.0;
    }
    return saturate(alpha);
#endif
}

vec3 uberCorrectNormal(vec3 normal, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    return normal;
#else
    vec3 objectOut = normalize(mesh.worldPos - (u_ModelMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xyz);
    float vertexMask = _NormalCorrectVertexColor == 0
        ? 1.0
        : mesh.vertexColor[clamp(_NormalCorrectVertexColor - 1, 0, 3)];
    return normalize(mix(normal, objectOut, saturate(_NormalCorrectStrength * vertexMask)));
#endif
}

#ifndef XRENGINE_UBER_DISABLE_LAYERED_DECALS
vec4 uberSampleDecal(
    sampler2D decalTexture,
    vec4 decalColor,
    vec2 position,
    vec2 scale,
    vec2 pan,
    float rotation,
    float rotationSpeed,
    int uvMode,
    int maskChannel,
    vec4 modifiers,
    vec4 effects,
    ToonMesh mesh)
{
    vec2 uv = getUV(uvMode, mesh);
    int mirrorMode = int(modifiers.z + 0.5);
    if (mirrorMode == 1 || (mirrorMode == 4 && mesh.viewDir.x < 0.0))
        uv.x = 1.0 - uv.x;
    uv = panUV(uv, pan, u_Time);
    uv = (uv - position) / max(abs(scale), vec2(EPSILON)) + vec2(0.5);
    uv = rotateUV(uv, radians(rotation + rotationSpeed * u_Time), vec2(0.5));
    float bounds = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
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
    decal.a *= uberChannel(texture(_DecalMask, maskUv), maskChannel) * bounds * faceMask * globalMask * depthFade;
    return decal;
}

void uberBlendDecal(
    inout FragmentData fragData,
    vec4 decal,
    vec4 parameters)
{
    int blendMode = int(parameters.x + 0.5);
    float opacity = saturate(decal.a * parameters.y);
    vec3 blended = uberBlendRgb(fragData.baseColor, decal.rgb, blendMode);
    fragData.baseColor = mix(fragData.baseColor, blended, opacity);
    fragData.alpha = mix(fragData.alpha, decal.a, saturate(parameters.z) * opacity);
    fragData.emission += decal.rgb * max(parameters.w, 0.0) * opacity;
}

#endif

void uberApplyDecals(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_LAYERED_DECALS
    uberBlendDecal(fragData, uberSampleDecal(_DecalTexture, _DecalColor, _DecalPosition, _DecalScale, _DecalTexturePan, _DecalRotation, _DecalRotationSpeed, _DecalUvMode0, 0, _DecalSlotModifiers0, _DecalSlotFx0, mesh), _DecalBlendParams0);
    uberBlendDecal(fragData, uberSampleDecal(_DecalTexture1, _DecalColor1, _DecalPosition1, _DecalScale1, _DecalTexturePan1, _DecalRotation1, _DecalRotationSpeed1, _DecalUvMode1, 1, _DecalSlotModifiers1, _DecalSlotFx1, mesh), _DecalBlendParams1);
    uberBlendDecal(fragData, uberSampleDecal(_DecalTexture2, _DecalColor2, _DecalPosition2, _DecalScale2, _DecalTexturePan2, _DecalRotation2, _DecalRotationSpeed2, _DecalUvMode2, 2, _DecalSlotModifiers2, _DecalSlotFx2, mesh), _DecalBlendParams2);
    uberBlendDecal(fragData, uberSampleDecal(_DecalTexture3, _DecalColor3, _DecalPosition3, _DecalScale3, _DecalTexturePan3, _DecalRotation3, _DecalRotationSpeed3, _DecalUvMode3, 3, _DecalSlotModifiers3, _DecalSlotFx3, mesh), _DecalBlendParams3);
#endif
}

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
    vec4 modifiers,
    vec4 sampling,
    ToonMesh mesh)
{
    vec2 baseUv = getUV(uvMode, mesh);
    vec2 uv = panUV(transformUV(baseUv, sourceTransform), pan, u_Time);
    vec2 maskUv = transformUV(baseUv, maskTransform);
    vec4 texel = texture(source, uv);
    vec4 maskTexel = texture(maskTexture, maskUv);
    float mask = maskTexel[int(clamp(sampling.y, 0.0, 3.0) + 0.5)];
    mask = mix(mask, 1.0 - mask, saturate(sampling.z));
    float pulse = mix(1.0, 0.5 + 0.5 * sin(u_Time * parameters.z), saturate(parameters.y));
    vec3 sourceColor = mix(texel.rgb, fragData.baseColor, saturate(sampling.x));
    vec3 shifted = parameters.w == 0.0 ? sourceColor : hueShift(sourceColor, parameters.w * u_Time);
    vec3 themedColor = uberResolveThemeColor(int(modifiers.w + 0.5), color.rgb);
    float centerOut = modifiers.y > 0.5
        ? smoothstep(0.0, 0.5, abs(fract(uv.x + u_Time * parameters.z) - 0.5))
        : 1.0;
    float globalMask = uberGlobalMask(int(modifiers.z + 0.5), mesh);
    vec3 emission = shifted * themedColor * max(parameters.x, 0.0) * mask * pulse * centerOut * globalMask;
    fragData.emission += emission;
    fragData.baseColor = mix(fragData.baseColor, emission, saturate(modifiers.x) * mask);
}

void uberApplyEmissionSlots(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_LAYERED_EMISSION
    uberEmissionSlot(fragData, _Emission0Tex, _Emission0Mask, _Emission0Tex_ST, _Emission0Mask_ST, _EmissionSlotColor0, _EmissionSlotParams0, _EmissionSlotPan0, _EmissionSlotUv0, _EmissionSlotModifiers0, _EmissionSlotSampling0, mesh);
    uberEmissionSlot(fragData, _Emission1Tex, _Emission1Mask, _Emission1Tex_ST, _Emission1Mask_ST, _EmissionSlotColor1, _EmissionSlotParams1, _EmissionSlotPan1, _EmissionSlotUv1, _EmissionSlotModifiers1, _EmissionSlotSampling1, mesh);
    uberEmissionSlot(fragData, _Emission2Tex, _Emission2Mask, _Emission2Tex_ST, _Emission2Mask_ST, _EmissionSlotColor2, _EmissionSlotParams2, _EmissionSlotPan2, _EmissionSlotUv2, _EmissionSlotModifiers2, _EmissionSlotSampling2, mesh);
    uberEmissionSlot(fragData, _Emission3Tex, _Emission3Mask, _Emission3Tex_ST, _Emission3Mask_ST, _EmissionSlotColor3, _EmissionSlotParams3, _EmissionSlotPan3, _EmissionSlotUv3, _EmissionSlotModifiers3, _EmissionSlotSampling3, mesh);
#endif
}

vec2 uberMatcapUv(ToonMesh mesh, vec4 parameters, vec4 projection)
{
    vec3 normal = normalize(mix(mesh.vertexNormal, mesh.worldNormal, saturate(parameters.w)));
    int uvMode = int(projection.z + 0.5);
    float border = max(projection.y, 0.0);

    if (uvMode == 1)
    {
        vec3 viewUp = normalize(vec3(0.0, 1.0, 0.0) - mesh.viewDir * dot(mesh.viewDir, vec3(0.0, 1.0, 0.0)));
        vec3 viewRight = normalize(cross(mesh.viewDir, viewUp));
        return vec2(dot(viewRight, normal), dot(viewUp, normal)) * border + 0.5;
    }

    if (uvMode == 2)
    {
        vec3 reflected = reflect(-mesh.viewDir, normal);
        return reflected.xy * border + 0.5;
    }

    vec3 viewNormal = normalize(mat3(u_ViewMatrix) * normal);
    return viewNormal.xy * border + 0.5;
}

vec4 uberMatcapSlot(
    sampler2D source,
    sampler2D maskTexture,
    vec4 color,
    int themeIndex,
    vec4 parameters,
    vec4 projection,
    ToonMesh mesh,
    ToonLight light,
    inout vec3 emission)
{
    vec2 uv = uberMatcapUv(mesh, parameters, projection);
    vec4 sampleColor = texture(source, uv);
    sampleColor.rgb *= uberResolveThemeColor(themeIndex, color.rgb);
    sampleColor.a *= color.a;
    float mask = texture(maskTexture, getUV(0, mesh)).r;
    mask *= mix(1.0, saturate(light.lightMap), saturate(parameters.y));
    sampleColor.rgb *= mix(vec3(1.0), light.color, saturate(projection.w));
    sampleColor.rgb *= max(parameters.x, 0.0);
    mask *= sampleColor.a;
    emission += sampleColor.rgb * max(parameters.z, 0.0) * mask;
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

void uberApplyMatcapSlots(inout vec3 finalColor, inout vec3 emission, ToonMesh mesh, ToonLight light)
{
#ifndef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
    vec4 value0 = uberMatcapSlot(_Matcap0Tex, _Matcap0Mask, _MatcapSlotColor0, _MatcapSlotTheme0, _MatcapSlotParams0, _MatcapSlotExtra0, mesh, light, emission);
    vec4 value1 = uberMatcapSlot(_Matcap1Tex, _Matcap1Mask, _MatcapSlotColor1, _MatcapSlotTheme1, _MatcapSlotParams1, _MatcapSlotExtra1, mesh, light, emission);
    vec4 value2 = uberMatcapSlot(_Matcap2Tex, _Matcap2Mask, _MatcapSlotColor2, _MatcapSlotTheme2, _MatcapSlotParams2, _MatcapSlotExtra2, mesh, light, emission);
    vec4 value3 = uberMatcapSlot(_Matcap3Tex, _Matcap3Mask, _MatcapSlotColor3, _MatcapSlotTheme3, _MatcapSlotParams3, _MatcapSlotExtra3, mesh, light, emission);
    uberBlendMatcapSlot(finalColor, value0, _MatcapSlotBlend0, _MatcapSlotExtra0.x);
    uberBlendMatcapSlot(finalColor, value1, _MatcapSlotBlend1, _MatcapSlotExtra1.x);
    uberBlendMatcapSlot(finalColor, value2, _MatcapSlotBlend2, _MatcapSlotExtra2.x);
    uberBlendMatcapSlot(finalColor, value3, _MatcapSlotBlend3, _MatcapSlotExtra3.x);
#endif
}

vec3 uberApplySecondRim(ToonMesh mesh, ToonLight light)
{
#ifdef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
    return vec3(0.0);
#else
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
    cubeMask = mix(cubeMask, 1.0 - cubeMask, saturate(_CubeMapMaskInvert));
    cubeMask *= mix(1.0, saturate(light.lightMap), saturate(_CubeMapLightMask));
    float cubeAlpha = saturate(cubeMask * cubeSample.a * _CubeMapBlendAmount);
    if (_CubeMapBlendType == 1)
        finalColor *= mix(vec3(1.0), environment, cubeAlpha);
    else if (_CubeMapBlendType == 2)
        finalColor += environment * cubeAlpha;
    else
        finalColor = mix(finalColor, environment, cubeAlpha);
    emission += environment * max(_CubeMapEmissionStrength, 0.0) * cubeMask * cubeSample.a;

    float rim = pow(1.0 - saturate(dot(mesh.worldNormal, mesh.viewDir)),
        mix(0.25, 16.0, saturate(_RimEnviroSharpness)));
    rim = smoothstep(1.0 - _RimEnviroWidth, 1.0, rim);
    finalColor += environment * rim * _RimEnviroIntensity;

    float backlight = saturate(dot(-mesh.worldNormal, light.direction));
    float backMask = texture(_BacklightMask, mesh.uv[0]).r;
    finalColor += _BacklightColor.rgb * backlight * backMask * _BacklightStrength;
    return finalColor;
#endif
}

void uberApplyFlipbookArray(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_TEXTURE_ARRAY_FLIPBOOK
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
