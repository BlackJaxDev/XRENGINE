#ifndef XR_UBER_SOURCE_SHADING_GLSL
#define XR_UBER_SOURCE_SHADING_GLSL

vec3 xrSourceEmissionFluorescence(vec3 directColor, float strength)
{
    if (strength == 0.0) return vec3(1.0);
    vec3 response = clamp((vec3(1.0) - directColor) *
        sqrt(max(directColor, vec3(0.0))), vec3(0.0), vec3(1.0));
    return mix(vec3(1.0), response, strength);
}

vec3 xrSourceDirectColor(vec3 color, float minimumBrightness,
    float monochromatic, float capEnabled, float cap)
{
    color = max(color, vec3(0.0001));
    float luminance = dot(color, vec3(0.299, 0.587, 0.114));
    color = max(color, color * min(10000.0, minimumBrightness / max(luminance, 0.000001)));
    luminance = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(color, vec3(luminance), clamp(monochromatic, 0.0, 1.0));
    if (capEnabled > 0.5) color = min(color, vec3(cap));
    return max(color, vec3(0.0));
}

float xrSourceLuminance(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

float xrSourceMean(vec3 color)
{
    return (color.r + color.g + color.b) * 0.3333333333333333;
}

struct XrSourceShCoefficients
{
    vec4 ar, ag, ab, br, bg, bb, c;
};

vec3 xrSourceShL2(XrSourceShCoefficients sh, vec4 normal)
{
    vec4 product = normal.xyzz * normal.yzzx;
    return vec3(dot(sh.br, product), dot(sh.bg, product),
        dot(sh.bb, product)) + sh.c.rgb * (normal.x * normal.x -
        normal.y * normal.y);
}

vec3 xrSourceShLinear(XrSourceShCoefficients sh, vec4 normal)
{
    return vec3(dot(sh.ar, normal), dot(sh.ag, normal),
        dot(sh.ab, normal)) + xrSourceShL2(sh, normal);
}

float xrSourceShGeomerics(float l0, vec3 l1, vec3 normal)
{
    float r0 = max(l0, 0.0);
    float r = length(l1) * 0.5;
    if (r0 <= 0.000001) return 0.0;
    if (r <= 0.000001) return r0;
    float ratio = r / r0;
    float q = clamp(dot(l1 / (2.0 * r), normal) * 0.5 + 0.5,
        0.0, 1.0);
    float power = 1.0 + 2.0 * ratio;
    float a = (1.0 - ratio) / (1.0 + ratio);
    return r0 * (a + (1.0 - a) * (power + 1.0) * pow(q, power));
}

vec3 xrSourceBetterSh9(XrSourceShCoefficients sh, vec4 normal)
{
    vec3 l0 = vec3(sh.ar.w, sh.ag.w, sh.ab.w) +
        vec3(sh.br.z, sh.bg.z, sh.bb.z) / 3.0;
    vec3 first = max(vec3(
        xrSourceShGeomerics(l0.r, sh.ar.xyz, normal.xyz),
        xrSourceShGeomerics(l0.g, sh.ag.xyz, normal.xyz),
        xrSourceShGeomerics(l0.b, sh.ab.xyz, normal.xyz)), vec3(0.0));
    return first + xrSourceShL2(sh, normal);
}

float xrSourceShLightMap(XrSourceShCoefficients sh, vec3 normal,
    vec3 directBeforeColor, float nl, float shadowVisibility,
    float castedShadows)
{
    vec3 shLength = vec3(length(sh.ar) + length(sh.br),
        length(sh.ag) + length(sh.bg), length(sh.ab) + length(sh.bb));
    vec3 shMinus = vec3(sh.ar.w, sh.ag.w, sh.ab.w) +
        vec3(sh.br.z, sh.bg.z, sh.bb.z) / 3.0;
    float directMean = xrSourceMean(directBeforeColor);
    float width = xrSourceMean(shLength) + directMean - xrSourceMean(shMinus);
    float shadow = mix(1.0, shadowVisibility,
        clamp(castedShadows, 0.0, 1.0));
    float value = (nl * 0.5 + 0.5) * directMean * shadow +
        xrSourceMean(xrSourceShLinear(sh, vec4(normal, 1.0))) -
        xrSourceMean(shMinus);
    return width > 0.000001 ? smoothstep(0.0, width, value) :
        (nl * 0.5 + 0.5) * shadow;
}

vec3 xrSourceShWrapped(XrSourceShCoefficients sh, vec3 normal,
    float wrap, float normalization)
{
    float saturated = clamp(wrap, 0.0, 1.0);
    vec3 convolution = mix(vec3(wrap + 1.0,
            -(wrap - 2.0) * (wrap + 1.0) / 3.0,
            0.25 * (saturated - 1.0) * (saturated - 1.0) *
                (saturated + 1.0)),
        vec3(1.0, 2.0 / 3.0 - wrap / 3.0,
            0.25 - 0.5 * saturated + 0.25 * saturated * saturated),
        clamp(normalization, 0.0, 1.0)) * vec3(1.0, 1.5, 4.0);
    vec3 l0 = vec3(sh.ar.w, sh.ag.w, sh.ab.w) +
        vec3(sh.br.z, sh.bg.z, sh.bb.z) / 3.0;
    vec3 l1 = vec3(dot(sh.ar.xyz, normal), dot(sh.ag.xyz, normal),
        dot(sh.ab.xyz, normal));
    vec3 l2 = xrSourceShL2(sh, vec4(normal, 1.0)) -
        vec3(sh.br.z, sh.bg.z, sh.bb.z) / 3.0;
    return l0 * convolution.x + l1 * convolution.y + l2 * convolution.z;
}

void xrSourceShLight(XrSourceShCoefficients sh, vec3 rawPrimary,
    vec3 normal, vec3 originalDirection, int mode, float unlitIntensity,
    float indirectUsesNormals, out vec3 direction, out vec3 direct,
    out vec3 indirect)
{
    vec3 shDirection = (sh.ar.xyz + sh.ag.xyz + sh.ab.xyz) / 3.0;
    vec3 weightedDirection = xrSourceLuminance(rawPrimary) * originalDirection +
        shDirection;
    direction = dot(weightedDirection, weightedDirection) > 0.0
        ? normalize(weightedDirection) : normalize(vec3(0.4, 1.0, 0.4));
    if (mode == 2)
    {
        indirect = clamp(max(vec3(0.05 * unlitIntensity),
            max(xrSourceShLinear(sh, vec4(0.0, 0.0, 0.0, 1.0)),
                xrSourceShLinear(sh, vec4(0.0, -1.0, 0.0, 1.0))) *
                unlitIntensity), 0.0, 1.0);
        direct = max(indirect, rawPrimary);
        return;
    }
    vec4 magicDirection = sh.ar + sh.ag + sh.ab;
    magicDirection = dot(magicDirection, magicDirection) > 0.0
        ? normalize(magicDirection) : vec4(0.0);
    vec3 magic = max(xrSourceBetterSh9(sh, magicDirection), vec3(0.0));
    vec3 standard = rawPrimary + xrSourceBetterSh9(sh, vec4(0.0, 0.0, 0.0, 1.0));
    float magicLuma = xrSourceLuminance(magic);
    float standardLuma = xrSourceLuminance(standard);
    float totalLuma = magicLuma + standardLuma;
    float target = totalLuma > 0.000001
        ? (magicLuma * magicLuma + standardLuma * standardLuma) / totalLuma
        : 0.0;
    vec3 combined = magic + standard;
    direct = combined * max(0.0001, target /
        max(xrSourceLuminance(combined), 0.000001));
    indirect = xrSourceBetterSh9(sh, vec4(normal * indirectUsesNormals, 1.0));
}

void xrSourceConstantAmbientLight(vec3 ambient, vec3 primary, int mode,
    float unlitIntensity, out vec3 direct, out vec3 indirect)
{
    if (mode == 2)
    {
        indirect = clamp(max(vec3(0.05), ambient) * unlitIntensity, 0.0, 1.0);
        direct = max(indirect, primary);
        return;
    }
    indirect = ambient;
    vec3 standard = ambient + primary;
    vec3 combined = ambient + standard;
    float ambientLuma = xrSourceLuminance(ambient);
    float standardLuma = xrSourceLuminance(standard);
    float totalLuma = ambientLuma + standardLuma;
    float target = totalLuma > 0.000001
        ? (ambientLuma * ambientLuma + standardLuma * standardLuma) / totalLuma
        : 0.0;
    float combinedLuma = xrSourceLuminance(combined);
    direct = combined * max(0.0001, target / max(combinedLuma, 0.000001));
}

vec3 xrSourceAdditiveColor(vec3 raw, float attenuation, float limited,
    float limit, float monochromatic)
{
    vec3 color = raw * attenuation;
    if (limited > 0.5)
        color *= min(1.0, limit / max(xrSourceLuminance(color), 0.00001));
    float luma = xrSourceLuminance(color);
    return mix(color, vec3(luma), clamp(monochromatic, 0.0, 1.0));
}

void xrSourceAdditiveColors(vec3 raw, float attenuation, vec4 settings,
    vec4 colorSettings, bool cookie, out vec3 direct, out vec3 indirect)
{
    direct = raw * attenuation;
    if (settings.z > 0.5)
        direct *= min(1.0, settings.w / max(xrSourceLuminance(direct), 0.00001));
    indirect = cookie ? vec3(0.0) : direct * colorSettings.y;
    if (settings.z > 0.5)
        indirect *= min(1.0, settings.w / max(xrSourceLuminance(indirect), 0.00001));
    direct = mix(direct, vec3(xrSourceLuminance(direct)),
        clamp(colorSettings.x, 0.0, 1.0));
    indirect = mix(indirect, vec3(xrSourceLuminance(indirect)),
        clamp(colorSettings.x, 0.0, 1.0));
}

vec3 xrSourceAdditiveDiffuse(vec3 base, vec3 direct, vec3 indirect,
    vec3 ramp, vec3 shadowColor, float nl, float visibility,
    float shadowMask, float materialAo, float shadowStrength,
    float ignoreAmbient, int shadingMode, vec4 additiveColor,
    vec4 gradient)
{
    int additiveType = int(additiveColor.z + 0.5);
    float shadow = mix(1.0, visibility, clamp(additiveColor.w, 0.0, 1.0));
    if (additiveType == 0 || (additiveType == 3 && shadingMode == 2))
        return base * direct * max(nl, 0.0) * shadow;
    if (additiveType == 1)
    {
        float gradientEnd = max(gradient.y, gradient.x + 0.0001);
        float band = smoothstep(gradient.x, gradientEnd, 1.0 - (nl * 0.5 + 0.5));
        return base * mix(direct * max(min(visibility, 1.0), additiveColor.y),
            indirect, band);
    }
    if (shadingMode == 5)
        return base * direct * shadow;
    float rampStrength = clamp(shadowStrength * shadowMask, 0.0, 1.0);
    vec3 ramped = mix(vec3(1.0), ramp, rampStrength);
    vec3 shaded = shadowColor * mix(indirect, ramped * direct,
        clamp(ignoreAmbient, 0.0, 1.0)) * materialAo;
    return base * mix(shaded, direct, ramped);
}

float xrSourceLightMap(float nl, int mode, float castedShadows,
    float shadowVisibility)
{
    float normalized = nl * 0.5 + 0.5;
    float shadow = mix(1.0, shadowVisibility, clamp(castedShadows, 0.0, 1.0));
    if (mode == 0) return smoothstep(0.0, 1.0, normalized * shadow);
    if (mode == 1) return normalized * shadow;
    if (mode == 2) return max(nl, 0.0) * shadow;
    return shadow;
}

float xrSourceAdditiveLightMap(float nl, int mode, float visibility,
    float castedShadows)
{
    float map = mode == 3 ? 1.0 : nl * 0.5 + 0.5;
    return map * mix(1.0, visibility, clamp(castedShadows, 0.0, 1.0));
}

vec3 xrSourceRampDiffuse(vec3 baseColor, vec3 direct, vec3 indirect,
    vec3 ramp, vec3 shadowColor, float shadowStrength, float shadowMask,
    float ignoreAmbient, float materialAo)
{
    vec3 ramped = mix(vec3(1.0), ramp,
        clamp(shadowStrength * shadowMask, 0.0, 1.0));
    vec3 shadow = shadowColor * mix(indirect, ramped * direct,
        clamp(ignoreAmbient, 0.0, 1.0)) * materialAo;
    return baseColor * mix(shadow, direct, ramped);
}

float xrSourceWrappedMap(vec3 normal, vec3 light, vec3 rawDirect,
    XrSourceShCoefficients sh, float wrapAmount, float normalization,
    float materialAo, float shadowVisibility, float castedShadows,
    float detailShadow, float monochromatic)
{
    float clampedWrap = clamp(wrapAmount, 0.0, 1.0);
    float wrapped = (dot(normal, light) + clampedWrap) /
        max((1.0 + clampedWrap) *
            (1.0 + clampedWrap * normalization), 0.000001);
    if (wrapAmount > 1.0)
        wrapped = mix(wrapped, 1.0 - 0.85 * normalization,
            clamp(wrapAmount - 1.0, 0.0, 1.0));
    vec3 direct = rawDirect * clamp(wrapped, 0.0, 1.0);
    vec3 indirect = xrSourceShWrapped(sh, normal, wrapAmount,
        normalization) * materialAo;
    direct = mix(direct, vec3(xrSourceLuminance(direct)),
        clamp(monochromatic, 0.0, 1.0));
    indirect = mix(indirect, vec3(xrSourceLuminance(indirect)),
        clamp(monochromatic, 0.0, 1.0));
    vec3 shDirection = sh.ar.xyz + sh.ag.xyz + sh.ab.xyz;
    shDirection = dot(shDirection, shDirection) > 0.0
        ? normalize(shDirection) : vec3(0.0);
    float top = xrSourceMean(xrSourceShWrapped(sh, shDirection, 0.0,
        normalization)) + xrSourceMean(rawDirect);
    float map = top > 0.000001
        ? smoothstep(0.0, top, xrSourceMean(direct + indirect)) : 0.0;
    float shadow = mix(1.0, shadowVisibility,
        clamp(castedShadows, 0.0, 1.0));
    return map * min(detailShadow, shadow);
}

vec3 xrSourceWrappedDiffuse(vec3 baseColor, vec3 normal, vec3 light,
    vec3 rawDirect, XrSourceShCoefficients sh, vec3 wrappedColor,
    vec3 shadowColor, float wrapAmount, float normalization,
    float materialAo, float shadowVisibility, float castedShadows,
    float detailShadow, float monochromatic, float shadowStrength,
    float ignoreAmbient, float gradientStart, float gradientEnd,
    float minimumBrightness, float capEnabled, float cap,
    float mapWidth)
{
    float clampedWrap = clamp(wrapAmount, 0.0, 1.0);
    float wrapped = (dot(normal, light) + clampedWrap) /
        max((1.0 + clampedWrap) *
            (1.0 + clampedWrap * normalization), 0.000001);
    if (wrapAmount > 1.0)
        wrapped = mix(wrapped, 1.0 - 0.85 * normalization,
            clamp(wrapAmount - 1.0, 0.0, 1.0));
    vec3 direct = rawDirect * clamp(wrapped, 0.0, 1.0);
    vec3 indirect = xrSourceShWrapped(sh, normal, wrapAmount,
        normalization) * materialAo;
    direct = mix(direct, vec3(xrSourceLuminance(direct)),
        clamp(monochromatic, 0.0, 1.0));
    indirect = mix(indirect, vec3(xrSourceLuminance(indirect)),
        clamp(monochromatic, 0.0, 1.0));
    float map = xrSourceWrappedMap(normal, light, rawDirect, sh,
        wrapAmount, normalization, materialAo, shadowVisibility,
        castedShadows, detailShadow, monochromatic);
    float width = max(clamp(gradientEnd - gradientStart + mapWidth,
        0.0, 1.0), 0.000001);
    float ramp = clamp((map - (1.0 - gradientEnd)) / width, 0.0, 1.0);
    vec3 shadowTint = clamp(mix(shadowColor * mix(indirect, vec3(1.0),
        clamp(ignoreAmbient, 0.0, 1.0)), vec3(1.0), ramp), 0.0, 1.0);
    vec3 multiplier = mix(vec3(1.0), shadowTint, shadowStrength);
    direct *= clamp(ramp + 1.0 - shadowStrength, 0.0, 1.0) * wrappedColor;
    vec3 lighting = direct + indirect;
    lighting = capEnabled > 0.5
        ? clamp(lighting, vec3(minimumBrightness), vec3(cap))
        : max(lighting, vec3(minimumBrightness));
    return baseColor * lighting * clamp(multiplier + vec3(1.0 - shadowStrength),
        vec3(0.0), vec3(1.0));
}

float xrSourcePerceptualRoughness(float smoothness, vec3 normalDx, vec3 normalDy,
    vec4 settings)
{
    float perceptual = 1.0 - smoothness;
    if (settings.x > 0.5)
    {
        float variance = settings.y * (dot(normalDx, normalDx) + dot(normalDy, normalDy));
        perceptual = pow(clamp(pow(perceptual, 4.0) + min(2.0 * variance, settings.z),
            0.0, 1.0), 0.25);
    }
    return perceptual;
}

float xrSourceBrdfRoughness(float smoothness, vec3 normalDx, vec3 normalDy,
    vec4 settings)
{
    float perceptual = xrSourcePerceptualRoughness(smoothness,
        normalDx, normalDy, settings);
    return max(perceptual * perceptual, 0.002);
}

vec3 xrSourceGgxLobe(vec3 normal, vec3 view, vec3 light, vec3 f0,
    float smoothness, float mask, vec3 tint, vec3 radiance,
    float materialAo, float shadowVisibility, vec3 normalDx, vec3 normalDy,
    vec4 settings, float ignoreShadows, bool directional)
{
    float nl = dot(normal, light);
    if (nl <= 0.0 || mask <= 0.0) return vec3(0.0);
    vec3 halfVector = normalize(light + view);
    float nv = abs(dot(normal, view));
    float nh = dot(normal, halfVector);
    float lh = max(dot(light, halfVector), 0.00001);
    float alpha = xrSourceBrdfRoughness(smoothness, normalDx, normalDy, settings);
    float visibility = 0.5 / (nl * (nv * (1.0 - alpha) + alpha) +
        nv * (nl * (1.0 - alpha) + alpha) + 0.00001);
    float nhAlpha = nh * alpha;
    float k = alpha / (1.0 - nh * nh + nhAlpha * nhAlpha + 0.00001);
    float distribution = k * k * 0.3183098861837907;
    float term = max(0.0, visibility * distribution * max(0.00001, nl));
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - lh, 5.0);
    float castVisibility = mix(shadowVisibility, 1.0, clamp(ignoreShadows, 0.0, 1.0));
    if (directional) castVisibility = min(clamp(nl, 0.0, 1.0), castVisibility);
    return radiance * term * fresnel * mask * tint * materialAo * castVisibility;
}

vec3 xrSourceMochieLobe(vec3 normal, vec3 view, vec3 light, vec3 baseColor,
    float metallic, float smoothness, float mask, vec3 tint, vec3 radiance,
    float materialAo, float shadowVisibility, vec3 normalDx, vec3 normalDy,
    vec4 settings, float ignoreShadows, bool directional)
{
    return xrSourceGgxLobe(normal, view, light,
        mix(vec3(0.04), baseColor, metallic), smoothness, mask, tint, radiance,
        materialAo, shadowVisibility, normalDx, normalDy, settings,
        ignoreShadows, directional);
}

vec3 xrSourceClearCoatReflection(vec3 rawRadiance, vec3 directColor,
    float smoothness, float reflectionMask, float materialAo, float nv,
    vec3 normalDx, vec3 normalDy, vec4 settings, vec4 reflectionSettings)
{
    float alpha = xrSourceBrdfRoughness(smoothness, normalDx, normalDy, settings);
    vec3 f0 = vec3(0.220916301);
    vec3 f90 = f0 + mix(f0, vec3(1.0), reflectionSettings.x) * reflectionSettings.x;
    vec3 fresnel = mix(f0, f90, pow(1.0 - nv, 5.0));
    float visibility = clamp(length(directColor + vec3(0.000001)) /
        max(reflectionSettings.y, 0.000001), 0.0, 1.0);
    float sf = clamp(pow(nv + visibility, exp2(-16.0 * alpha - 1.0)) - 1.0 + visibility,
        0.0, 1.0);
    return rawRadiance * (1.0 / (alpha * alpha + 1.0)) * fresnel * sf *
        reflectionMask * materialAo;
}

vec3 xrSourceMochieReflection(vec3 rawRadiance, vec3 directColor,
    vec3 f0, float smoothness, float reflectionMask, float materialAo,
    float nv, vec3 normalDx, vec3 normalDy, vec4 gsaa,
    vec4 reflectionSettings, vec3 tint)
{
    float alpha = xrSourceBrdfRoughness(smoothness, normalDx, normalDy, gsaa);
    float fresnelStrength = reflectionSettings.x;
    vec3 grazing = f0 + mix(f0, vec3(1.0), fresnelStrength) *
        fresnelStrength;
    vec3 fresnel = mix(f0, grazing, pow(1.0 - nv, 5.0));
    float visibility = clamp(length(directColor + vec3(0.000001)) /
        max(reflectionSettings.y, 0.000001), 0.0, 1.0);
    float sf = clamp(pow(nv + visibility, exp2(-16.0 * alpha - 1.0)) -
        1.0 + visibility, 0.0, 1.0);
    return rawRadiance * (1.0 / (alpha * alpha + 1.0)) * fresnel * sf *
        reflectionMask * materialAo * tint;
}

vec3 xrSourceMochieDirect(vec3 normal, vec3 view, vec3 light, vec3 baseColor,
    float metallic, float smoothness, float packedSmoothness, float mask, vec3 tint,
    vec3 radiance, float materialAo, float shadowVisibility, vec3 normalDx,
    vec3 normalDy, vec4 settings, vec4 second, bool directional)
{
    vec3 main = xrSourceMochieLobe(normal, view, light, baseColor, metallic,
        smoothness, mask, tint, radiance, materialAo, shadowVisibility,
        normalDx, normalDy, settings, second.z, directional);
    if (settings.w <= 0.5 || second.y <= 0.0) return main;
    vec3 extra = xrSourceMochieLobe(normal, view, light, baseColor, metallic,
        packedSmoothness * second.x, mask * second.y, tint, radiance, materialAo,
        shadowVisibility, normalDx, normalDy, settings, second.z, directional);
    return max(main, extra);
}

float xrSourceRimAmount(vec3 vertexNormal, vec3 pixelNormal, vec3 view,
    float width, float sharpness, vec4 settings)
{
    vec3 normal = mix(vertexNormal, pixelNormal, settings.w);
    float angle = abs(dot(view, normal));
    if (settings.y > 0.5) angle = 1.0 - angle;
    angle = pow(angle, settings.z);
    float border = mix(-0.05, 1.0, width);
    return 1.0 - smoothstep(min(sharpness, border), border, angle);
}

vec3 xrSourceRimBlend(vec3 baseColor, vec3 rimColor, float amount, int mode)
{
    if (mode == 1) return mix(baseColor, rimColor, amount);
    if (mode == 2) return mix(baseColor, baseColor * rimColor, amount);
    if (mode == 3) return mix(baseColor, baseColor + baseColor * rimColor, amount);
    if (mode == 4)
        return mix(baseColor, 1.0 - (1.0 - baseColor) * (1.0 - rimColor), amount);
    return baseColor + rimColor * amount;
}

vec3 xrSourceAnisoLayer(vec3 tangent, vec3 binormal, vec3 normal,
    vec3 view, vec3 light, float shadowMask, vec4 layer, vec3 tint)
{
    vec3 strand = normalize(mix(binormal, tangent, layer.w) + layer.z * normal);
    vec3 halfVector = normalize(light + view);
    float tangentHalf = clamp(dot(strand, halfVector), -1.0, 1.0);
    float sine = sqrt(max(0.0, 1.0 - tangentHalf * tangentHalf));
    float response = clamp(smoothstep(-1.0, 0.0, tangentHalf) *
        pow(sine, layer.y * 1000.0) * layer.x, 0.0, 1.0);
    return response * shadowMask * tint;
}

void xrSourceApplyAnisotropy(vec3 geometricNormal, vec3 mappedNormal,
    vec3 tangent, vec3 bitangent, vec3 view, vec3 light, vec3 directColor,
    float rampedLightMap, vec4 settings, vec4 mixSettings,
    vec4 layer0, vec4 layer1, vec3 tint0, vec3 tint1,
    inout vec3 baseColor, inout vec3 lightAdd)
{
    if (settings.x <= 0.5) return;

    float normalBlend = settings.w;
    vec3 normal = mix(geometricNormal, mappedNormal, normalBlend);
    // The imported coordinate frame reflects the source Z axis. Reverse the
    // source cross products to keep the two strand axes in that frame.
    vec3 mappedTangent = cross(bitangent, mappedNormal);
    vec3 mappedBinormal = cross(mappedNormal, tangent);
    vec3 strandTangent = mix(tangent, mappedTangent, normalBlend);
    vec3 strandBinormal = mix(bitangent, mappedBinormal, normalBlend);
    float shadowMask = mix(1.0, rampedLightMap, mixSettings.z) *
        mix(1.0, dot(normal, light), mixSettings.w);
    vec3 color0 = xrSourceAnisoLayer(strandTangent, strandBinormal,
        normal, view, light, shadowMask, layer0, tint0);
    vec3 color1 = xrSourceAnisoLayer(strandTangent, strandBinormal,
        normal, view, light, shadowMask, layer1, tint1);
    vec3 lightColor = mix(vec3(dot(directColor, vec3(0.299, 0.587, 0.114))),
        directColor, mixSettings.y);
    vec3 result = (color0 + color1) *
        mix(vec3(1.0), baseColor, mixSettings.x) * lightColor;
    lightAdd += max(vec3(0.0), result * settings.y);
    float replaceMask = clamp(length(color0) + length(color1), 0.0, 1.0);
    baseColor = mix(baseColor, result, settings.z * replaceMask);
}
struct XrSourceMultilayerInputs
{
    vec4 firstColor;
    vec4 secondColor;
    vec4 thirdColor;
    vec4 borderColor;
    vec4 firstTexture;
    vec4 secondTexture;
    vec4 thirdTexture;
    vec4 blurTexture;
    vec4 borders;
    vec4 blurs;
    vec4 receive;
    vec4 mixSettings;
    vec4 borderMask;
    vec4 borderMaskX;
    vec4 borderMaskY;
    vec4 borderShift;
    vec4 borderShift2;
    vec4 options;
    vec4 flatSettings;
    float strengthMask;
    float flatLight;
};

float xrSourceMultilayerRemap(float value, vec2 limits)
{
    float upper = limits.y == limits.x ? limits.y + 0.001 : limits.y;
    return clamp((value - limits.x) / (upper - limits.x), 0.0, 1.0);
}

float xrSourceMultilayerEdge(float value, float border, float blur,
    float range, float footprint, bool nonlinear)
{
    float lower = clamp(border - blur * 0.5 - range, 0.0, 1.0);
    float upper = clamp(border + blur * 0.5, 0.0, 1.0);
    if (nonlinear)
        return smoothstep(lower - footprint, upper + footprint, value);
    return (value - lower) / max(clamp(upper - lower + footprint, 0.0, 1.0), 0.0001);
}

vec3 xrSourceMultilayerRemappedMask(vec4 sampleValue,
    XrSourceMultilayerInputs data)
{
    return vec3(
        xrSourceMultilayerRemap(sampleValue.r, data.borderShift.xy),
        xrSourceMultilayerRemap(sampleValue.g, data.borderShift.zw),
        xrSourceMultilayerRemap(sampleValue.b, data.borderShift2.xy));
}

vec4 xrSourceMultilayerBands(float lightMap, vec2 lightMapGradient,
    float shadowVisibility, vec2 visibilityGradient,
    XrSourceMultilayerInputs data)
{
    bool nonlinear = data.blurs.w > 0.5;
    vec3 receive = mix(vec3(1.0), vec3(shadowVisibility), data.receive.xyz);
    vec3 receiveX = mix(vec3(1.0),
        vec3(shadowVisibility + visibilityGradient.x), data.receive.xyz);
    vec3 receiveY = mix(vec3(1.0),
        vec3(shadowVisibility + visibilityGradient.y), data.receive.xyz);
    vec3 maps = vec3(lightMap) * receive;
    vec3 mapsX = vec3(lightMap + lightMapGradient.x) * receiveX;
    vec3 mapsY = vec3(lightMap + lightMapGradient.y) * receiveY;
    vec3 borderMask = data.borderMask.rgb;
    if (data.options.x > 0.5 && data.options.z <= 0.5)
    {
        maps *= borderMask;
        mapsX *= data.borderMaskX.rgb;
        mapsY *= data.borderMaskY.rgb;
    }
    if (data.mixSettings.w > 0.5)
    {
        maps = vec3(lightMap);
        mapsX = vec3(lightMap + lightMapGradient.x);
        mapsY = vec3(lightMap + lightMapGradient.y);
    }
    vec3 widths = abs(mapsX - maps) + abs(mapsY - maps);
    vec4 bands = vec4(
        xrSourceMultilayerEdge(maps.x, data.borders.x,
            data.blurs.x * data.blurTexture.r, 0.0, widths.x, nonlinear),
        xrSourceMultilayerEdge(maps.y, data.borders.y,
            data.blurs.y * data.blurTexture.g, 0.0, widths.y, nonlinear),
        xrSourceMultilayerEdge(maps.z, data.borders.z,
            data.blurs.z * data.blurTexture.b, 0.0, widths.z, nonlinear),
        xrSourceMultilayerEdge(maps.x, data.borders.x,
            data.blurs.x * data.blurTexture.r, data.borders.w, widths.x,
            nonlinear));
    if (data.options.x > 0.5 && data.options.z > 0.5)
        bands *= borderMask.rgbr;
    bands = clamp(bands, 0.0, 1.0);
    if (data.options.y > 0.5)
    {
        float flatBand = clamp((data.flatLight + data.flatSettings.x) /
            max(data.flatSettings.y, 0.0001), 0.0, 1.0);
        bands = mix(vec4(flatBand), bands, data.strengthMask);
    }
    return bands;
}

vec3 xrSourceMultilayerDiffuse(vec3 base, vec3 direct, vec3 indirect,
    float lightMap, vec2 lightMapGradient, float shadowVisibility,
    vec2 visibilityGradient, float shadowMask, bool primary,
    XrSourceMultilayerInputs data)
{
    vec4 bands = xrSourceMultilayerBands(lightMap, lightMapGradient,
        shadowVisibility, visibilityGradient, data);

    vec3 shade = vec3(1.0);
    if (data.firstColor.a > 0.0)
        shade = mix(vec3(1.0), data.firstTexture.rgb,
            data.firstTexture.a) * data.firstColor.rgb;
    if (data.secondColor.a > 0.0)
    {
        vec3 second = mix(vec3(1.0), data.secondTexture.rgb,
            data.secondTexture.a) * data.secondColor.rgb;
        shade = mix(shade, second,
            data.secondColor.a * (1.0 - bands.y));
    }
    if (data.thirdColor.a > 0.0)
    {
        vec3 third = mix(vec3(1.0), data.thirdTexture.rgb,
            data.thirdTexture.a) * data.thirdColor.rgb;
        shade = mix(shade, third,
            data.thirdColor.a * (1.0 - bands.z));
    }
    shade = mix(shade, shade * base, data.receive.w);
    shade = mix(shade, vec3(1.0),
        bands.w * data.borderColor.rgb * data.borderColor.a);
    shade *= mix(indirect, direct, data.mixSettings.z);
    if (primary)
        shade = mix(shade, direct, indirect * data.mixSettings.x);
    float strength = data.mixSettings.y * shadowMask * shadowMask;
    if (data.options.y <= 0.5)
        strength *= data.strengthMask;
    shade = mix(direct, shade, strength);
    return base * mix(shade, direct, bands.x);
}
#endif
