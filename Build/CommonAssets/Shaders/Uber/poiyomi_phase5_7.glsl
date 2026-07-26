#ifndef XRENGINE_POIYOMI_PHASE5_7_GLSL
#define XRENGINE_POIYOMI_PHASE5_7_GLSL

float poiChannel(vec4 value, int channel)
{
    return value[clamp(channel, 0, 3)];
}

vec3 poiBlendRgb(vec3 baseColor, vec3 layerColor, int mode)
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

vec2 poiResolveUv(int uvMode, ToonMesh mesh)
{
    return getUV(uvMode, mesh);
}

vec4 poiSampleStochastic(sampler2D source, vec2 uv, int mode)
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
        float seed = hash21(cell);
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
    vec2 offsetA = vec2(hash21(cellA), hash21(cellA + 17.0));
    vec2 offsetB = vec2(hash21(cellB), hash21(cellB + 17.0));
    vec2 offsetC = vec2(hash21(cellC), hash21(cellC + 17.0));
    vec3 weights = max(vec3(1.0 - local.x, abs(local.x - local.y), local.y), vec3(0.001));
    weights /= dot(weights, vec3(1.0));
    return textureGrad(source, uv + offsetA, dx, dy) * weights.x
         + textureGrad(source, uv + offsetB, dx, dy) * weights.y
         + textureGrad(source, uv + offsetC, dx, dy) * weights.z;
}

vec2 poiMainUv(ToonMesh mesh)
{
    vec2 uv = getUV(_MainTexUV, mesh);
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SURFACE
    uv = rotateUV(uv, radians(_MainTexRotation), vec2(0.5));
    vec2 distortionUv = transformUV(
        poiResolveUv(_MainTexDistortionMapUV, mesh),
        _MainTexDistortionMap_ST);
    distortionUv += _MainTexDistortionSpeed * u_Time;
    vec2 maskUv = transformUV(
        poiResolveUv(_MainTexDistortionMaskUV, mesh),
        _MainTexDistortionMask_ST);
    vec2 distortion = texture(_MainTexDistortionMap, distortionUv).rg * 2.0 - 1.0;
    float distortionMask = texture(_MainTexDistortionMask, maskUv).r;
    uv += distortion * (_MainTexDistortionStrength * distortionMask);
#endif
    return panUV(transformUV(uv, _MainTex_ST), _MainTexPan, u_Time);
}

vec4 poiSampleMainTexture(ToonMesh mesh)
{
    vec2 uv = poiMainUv(mesh);
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_SURFACE
    return texture(_MainTex, uv);
#else
    return poiSampleStochastic(_MainTex, uv, _MainTexStochasticMode);
#endif
}

vec3 poiApplyExtendedColorAdjustments(vec3 color)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_SURFACE
    return color;
#else
    color = (color - 0.5) * max(_MainContrast, 0.0) + 0.5;
    color = mix(color, vec3(luminance(color)), saturate(_MainGrayscale));
    color = mix(color, _MainColorReplace.rgb, saturate(_MainColorReplaceStrength) * _MainColorReplace.a);
    return color;
#endif
}

vec3 poiResolveThemeColor(int themeIndex, vec3 authoredColor)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_MASKS_THEMES
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
    hsv.z = max(0.0, hsv.z + adjustment.z);
    return hsvToRgb(hsv);
#endif
}

vec4 poiSampleGlobalMaskTexture(int textureIndex, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_MASKS_THEMES
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

float poiGlobalMask(int encodedIndex, ToonMesh mesh)
{
    if (encodedIndex <= 0)
        return 1.0;
    int zeroBased = encodedIndex - 1;
    vec4 mask = poiSampleGlobalMaskTexture(zeroBased / 4, mesh);
    return mask[zeroBased % 4];
}

vec3 poiApplyColorMask(vec3 baseColor, ToonMesh mesh, inout vec3 emission, inout PBRData pbr)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_MASKS_THEMES
    return baseColor;
#else
    vec2 uv = transformUV(getUV(_ColorMaskUV, mesh), _ColorMask_ST);
    vec4 mask = texture(_ColorMask, uv);
    vec4 colors[4] = vec4[4](_ColorMaskColor0, _ColorMaskColor1, _ColorMaskColor2, _ColorMaskColor3);
    for (int index = 0; index < 4; ++index)
    {
        float weight = saturate(mask[index] * colors[index].a);
        vec3 themed = poiResolveThemeColor(int(_ColorMaskThemeIndices[index] + 0.5), colors[index].rgb);
        vec3 blended = poiBlendRgb(baseColor, themed, int(_ColorMaskBlendModes[index] + 0.5));
        baseColor = mix(baseColor, blended, weight);
        emission += themed * (_ColorMaskEmission[index] * weight);
        pbr.metallic = mix(pbr.metallic, _ColorMaskMetallic[index], weight);
        pbr.perceptualRoughness = mix(pbr.perceptualRoughness, 1.0 - _ColorMaskSmoothness[index], weight);
    }
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    return baseColor;
#endif
}

vec3 poiApplyColorMaskNormal(vec3 normal, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_MASKS_THEMES
    return normal;
#else
    vec2 uv = transformUV(getUV(_ColorMaskUV, mesh), _ColorMask_ST);
    vec4 mask = texture(_ColorMask, uv);
    float correction = dot(mask, saturate(_ColorMaskNormalStrength));
    return normalize(mix(normal, mesh.vertexNormal, saturate(correction)));
#endif
}

float poiInterleavedGradientNoise(vec2 pixel, float frame)
{
    return fract(52.9829189 * fract(dot(pixel + frame, vec2(0.06711056, 0.00583715))));
}

float poiApplyExtendedAlpha(float alpha, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_SURFACE
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
        float screenNoise = poiInterleavedGradientNoise(gl_FragCoord.xy, floor(u_Time * _AlphaDitherSpeed));
        float objectNoise = hash21(floor(mesh.localPos.xy * 64.0));
        float threshold = mix(screenNoise, objectNoise, saturate(_AlphaDitherGradient));
        alpha = alpha >= mix(1.0, threshold, saturate(_AlphaDither)) ? alpha : 0.0;
    }
    return saturate(alpha);
#endif
}

vec3 poiCorrectNormal(vec3 normal, ToonMesh mesh)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_SURFACE
    return normal;
#else
    vec3 objectOut = normalize(mesh.worldPos - (u_ModelMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xyz);
    float vertexMask = _NormalCorrectVertexColor == 0
        ? 1.0
        : mesh.vertexColor[clamp(_NormalCorrectVertexColor - 1, 0, 3)];
    return normalize(mix(normal, objectOut, saturate(_NormalCorrectStrength * vertexMask)));
#endif
}

vec4 poiSampleDecal(
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
    vec4 themedColor = vec4(poiResolveThemeColor(int(effects.w + 0.5), decalColor.rgb), decalColor.a);
    vec4 decal = sampled * themedColor;
    vec2 maskUv = panUV(transformUV(getUV(_DecalMaskUV, mesh), _DecalMask_ST), _DecalMaskPan, u_Time);
    float faceMask = modifiers.x == 1.0
        ? step(0.0, mesh.isFrontFace)
        : (modifiers.x == 2.0 ? step(mesh.isFrontFace, 0.0) : 1.0);
    float globalMask = poiGlobalMask(int(modifiers.y + 0.5), mesh);
    float depthFade = modifiers.w == 0.0
        ? 1.0
        : smoothstep(-0.5, 2.0, modifiers.w + dot(mesh.worldNormal, mesh.viewDir));
    decal.a *= poiChannel(texture(_DecalMask, maskUv), maskChannel) * bounds * faceMask * globalMask * depthFade;
    return decal;
}

void poiBlendDecal(
    inout FragmentData fragData,
    vec4 decal,
    vec4 parameters)
{
    int blendMode = int(parameters.x + 0.5);
    float opacity = saturate(decal.a * parameters.y);
    vec3 blended = poiBlendRgb(fragData.baseColor, decal.rgb, blendMode);
    fragData.baseColor = mix(fragData.baseColor, blended, opacity);
    fragData.alpha = mix(fragData.alpha, decal.a, saturate(parameters.z) * opacity);
    fragData.emission += decal.rgb * max(parameters.w, 0.0) * opacity;
}

void poiApplyDecals(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_DECALS
    poiBlendDecal(fragData, poiSampleDecal(_DecalTexture, _DecalColor, _DecalPosition, _DecalScale, _DecalTexturePan, _DecalRotation, _DecalRotationSpeed, _DecalUvMode0, 0, _DecalSlotModifiers0, _DecalSlotFx0, mesh), _DecalBlendParams0);
    poiBlendDecal(fragData, poiSampleDecal(_DecalTexture1, _DecalColor1, _DecalPosition1, _DecalScale1, _DecalTexturePan1, _DecalRotation1, _DecalRotationSpeed1, _DecalUvMode1, 1, _DecalSlotModifiers1, _DecalSlotFx1, mesh), _DecalBlendParams1);
    poiBlendDecal(fragData, poiSampleDecal(_DecalTexture2, _DecalColor2, _DecalPosition2, _DecalScale2, _DecalTexturePan2, _DecalRotation2, _DecalRotationSpeed2, _DecalUvMode2, 2, _DecalSlotModifiers2, _DecalSlotFx2, mesh), _DecalBlendParams2);
    poiBlendDecal(fragData, poiSampleDecal(_DecalTexture3, _DecalColor3, _DecalPosition3, _DecalScale3, _DecalTexturePan3, _DecalRotation3, _DecalRotationSpeed3, _DecalUvMode3, 3, _DecalSlotModifiers3, _DecalSlotFx3, mesh), _DecalBlendParams3);
#endif
}

void poiEmissionSlot(
    inout FragmentData fragData,
    sampler2D source,
    sampler2D maskTexture,
    vec4 color,
    vec4 parameters,
    vec2 pan,
    int uvMode,
    vec4 modifiers,
    ToonMesh mesh)
{
    vec2 uv = panUV(getUV(uvMode, mesh), pan, u_Time);
    vec4 texel = texture(source, uv);
    float mask = texture(maskTexture, uv).r;
    float pulse = mix(1.0, 0.5 + 0.5 * sin(u_Time * parameters.z), saturate(parameters.y));
    vec3 shifted = parameters.w == 0.0 ? texel.rgb : hueShift(texel.rgb, parameters.w * u_Time);
    vec3 themedColor = poiResolveThemeColor(int(modifiers.w + 0.5), color.rgb);
    float centerOut = modifiers.y > 0.5
        ? smoothstep(0.0, 0.5, abs(fract(uv.x + u_Time * parameters.z) - 0.5))
        : 1.0;
    float globalMask = poiGlobalMask(int(modifiers.z + 0.5), mesh);
    vec3 emission = shifted * themedColor * max(parameters.x, 0.0) * mask * pulse * centerOut * globalMask;
    fragData.emission += emission;
    fragData.baseColor = mix(fragData.baseColor, emission, saturate(modifiers.x) * mask);
}

void poiApplyEmissionSlots(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_EMISSION_SLOTS
    poiEmissionSlot(fragData, _Emission0Tex, _Emission0Mask, _EmissionSlotColor0, _EmissionSlotParams0, _EmissionSlotPan0, _EmissionSlotUv0, _EmissionSlotModifiers0, mesh);
    poiEmissionSlot(fragData, _Emission1Tex, _Emission1Mask, _EmissionSlotColor1, _EmissionSlotParams1, _EmissionSlotPan1, _EmissionSlotUv1, _EmissionSlotModifiers1, mesh);
    poiEmissionSlot(fragData, _Emission2Tex, _Emission2Mask, _EmissionSlotColor2, _EmissionSlotParams2, _EmissionSlotPan2, _EmissionSlotUv2, _EmissionSlotModifiers2, mesh);
    poiEmissionSlot(fragData, _Emission3Tex, _Emission3Mask, _EmissionSlotColor3, _EmissionSlotParams3, _EmissionSlotPan3, _EmissionSlotUv3, _EmissionSlotModifiers3, mesh);
#endif
}

vec3 poiMatcapSlot(
    sampler2D source,
    sampler2D maskTexture,
    vec4 color,
    vec4 parameters,
    vec2 uv,
    ToonMesh mesh,
    ToonLight light,
    inout vec3 emission)
{
    vec4 sampleColor = texture(source, uv) * color;
    float mask = texture(maskTexture, getUV(0, mesh)).r;
    mask *= mix(1.0, saturate(light.lightMap), saturate(parameters.z));
    vec3 value = sampleColor.rgb * max(parameters.x, 0.0) * mask;
    emission += value * max(parameters.w, 0.0);
    return value;
}

void poiApplyMatcapSlots(inout vec3 finalColor, inout vec3 emission, ToonMesh mesh, ToonLight light)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_MATCAP_RIM_SLOTS
    vec3 viewNormal = normalize(mat3(u_ViewMatrix) * mesh.worldNormal);
    vec2 uv = viewNormal.xy * 0.5 + 0.5;
    vec3 value0 = poiMatcapSlot(_Matcap0Tex, _Matcap0Mask, _MatcapSlotColor0, _MatcapSlotParams0, uv, mesh, light, emission);
    vec3 value1 = poiMatcapSlot(_Matcap1Tex, _Matcap1Mask, _MatcapSlotColor1, _MatcapSlotParams1, uv, mesh, light, emission);
    vec3 value2 = poiMatcapSlot(_Matcap2Tex, _Matcap2Mask, _MatcapSlotColor2, _MatcapSlotParams2, uv, mesh, light, emission);
    vec3 value3 = poiMatcapSlot(_Matcap3Tex, _Matcap3Mask, _MatcapSlotColor3, _MatcapSlotParams3, uv, mesh, light, emission);
    finalColor = mix(finalColor, poiBlendRgb(finalColor, value0, int(_MatcapSlotParams0.y + 0.5)), saturate(_MatcapSlotParams0.x));
    finalColor = mix(finalColor, poiBlendRgb(finalColor, value1, int(_MatcapSlotParams1.y + 0.5)), saturate(_MatcapSlotParams1.x));
    finalColor = mix(finalColor, poiBlendRgb(finalColor, value2, int(_MatcapSlotParams2.y + 0.5)), saturate(_MatcapSlotParams2.x));
    finalColor = mix(finalColor, poiBlendRgb(finalColor, value3, int(_MatcapSlotParams3.y + 0.5)), saturate(_MatcapSlotParams3.x));
#endif
}

vec3 poiApplySecondRim(ToonMesh mesh, ToonLight light)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_MATCAP_RIM_SLOTS
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

vec3 poiApplyDepthRim(ToonMesh mesh)
{
#if defined(XRENGINE_UBER_DISABLE_POIYOMI_MATCAP_RIM_SLOTS) || defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING) || defined(XRENGINE_UBER_DISABLE_FORWARD_SHADOWS) || defined(XRENGINE_UBER_DISABLE_FORWARD_CONTACT_SHADOWS)
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

vec3 poiApplyPbrParity(
    ToonMesh mesh,
    ToonLight light,
    PBRData pbr,
    vec3 finalColor)
{
#ifdef XRENGINE_UBER_DISABLE_POIYOMI_PBR_PARITY
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
    vec3 environment = textureLod(_CubeMap, reflected, pbr.perceptualRoughness * 8.0).rgb * _CubeMapColor.rgb;
    if (_StylizedReflectionMode == 0)
        environment = floor(environment * 4.0 + 0.5) * 0.25;
    else
        environment = smoothstep(vec3(0.15), vec3(0.85), environment);
    finalColor += environment * (_CubeMapStrength * pbr.reflectionMask);

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

void poiApplyFlipbookArray(inout FragmentData fragData, ToonMesh mesh)
{
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_FLIPBOOK_ARRAY
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
    vec3 blended = poiBlendRgb(fragData.finalColor, flipbook.rgb, _FlipbookBlendType);
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
