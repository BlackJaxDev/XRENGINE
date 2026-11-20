// Fragment body for Uber-shaded Gaussian avatar splats.
// Compiled after runtime injection of NativeUberMaterialSchema and AdvancedStandardMaterialShaderContract defines.
// With XR_SPLAT_SHADE_PER_SPLAT, the vertex shader shades each splat once and passes the result in FragSplatShade;
// this shader then only applies the Gaussian falloff and the blend. Without it, this shader shades each pixel.

#include "Gaussian/GaussianUberAdapter.glslinc"
#include "Advanced/Shading/ShadeNativeUber.glslinc"
#include "Gaussian/GaussianSplatUberShading.glslinc"

layout(location = 0) in vec2 FragEllipseOffset;
layout(location = 1) in vec3 FragWorldPosition;
layout(location = 2) in vec3 FragLocalPosition;
layout(location = 3) in vec3 FragWorldNormal;
layout(location = 4) in vec3 FragWorldTangent;
layout(location = 5) in vec4 FragUv0Uv1;
layout(location = 6) in vec4 FragUv2Uv3;
layout(location = 7) flat in float FragTangentHandedness;
layout(location = 8) flat in float FragBaseOpacity;
layout(location = 9) flat in uvec2 FragMaterialAndColor;
layout(location = 10) flat in vec4 FragSplatShade;

layout(location = 0) out vec4 OutColor;

const float MaxExponent = 4.84;
const float TaperStartExponent = 3.15;
const float MinAlpha = 1.0 / 64.0;

void main()
{
    // Evaluate screen-space UV derivatives in uniform quad control flow before any discard or material branch.
    vec2 uv0Dx = dFdx(FragUv0Uv1.xy);
    vec2 uv0Dy = dFdy(FragUv0Uv1.xy);
    vec2 uv1Dx = dFdx(FragUv0Uv1.zw);
    vec2 uv1Dy = dFdy(FragUv0Uv1.zw);
    vec2 uv2Dx = dFdx(FragUv2Uv3.xy);
    vec2 uv2Dy = dFdy(FragUv2Uv3.xy);
    vec2 uv3Dx = dFdx(FragUv2Uv3.zw);
    vec2 uv3Dy = dFdy(FragUv2Uv3.zw);

    float r2 = dot(FragEllipseOffset, FragEllipseOffset);
#if defined(XR_SPLAT_DEPTH_OCCLUSION_PASS)
    if (r2 > 1.96 || FragBaseOpacity < 0.5)
        discard;

    vec3 cameraPosition = InverseViewMatrix[3].xyz;
    vec3 shadingNormal = dot(FragWorldNormal, FragWorldNormal) > 1.0e-10
        ? normalize(FragWorldNormal)
        : vec3(0.0, 1.0, 0.0);
    bool backFacing = dot(shadingNormal, cameraPosition - FragWorldPosition) < 0.0;

    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(FragMaterialAndColor.x);
    if (XR_UBER_IsMaterial(material))
    {
        XR_UBER_Material = material;
        int uberBlendMode = UBER_I(_Mode);
        if (uberBlendMode != 0 && uberBlendMode != 1)
            discard;

        XRUberCoordinates coords;
        coords.uv0 = FragUv0Uv1.xy;
        coords.uv1 = FragUv0Uv1.zw;
        coords.uv2 = FragUv2Uv3.xy;
        coords.uv3 = FragUv2Uv3.zw;
        coords.uv0Dx = uv0Dx;
        coords.uv0Dy = uv0Dy;
        coords.uv1Dx = uv1Dx;
        coords.uv1Dy = uv1Dy;
        coords.uv2Dx = uv2Dx;
        coords.uv2Dy = uv2Dy;
        coords.uv3Dx = uv3Dx;
        coords.uv3Dy = uv3Dy;
        coords.worldPosition = FragWorldPosition;
        coords.localPosition = FragLocalPosition;
        coords.flatNormal = vec3(0.0);
        coords.color = unpackUnorm4x8(FragMaterialAndColor.y);
        coords.frontFacing = !backFacing;
        coords.time = EngineTime;

        vec4 baseCoverage;
        vec3 emissionCoverage;
        if (!XR_UBER_ApplyCoverage(coords, baseCoverage, emissionCoverage))
            discard;
    }
    else
    {
        if (material.coverageMode != 0u && material.coverageMode != 1u)
            discard;

        if (material.coverageMode == 1u)
        {
            uint materialFlags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
            vec4 base = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD) * unpackUnorm4x8(FragMaterialAndColor.y);
            if ((materialFlags & 1u) != 0u)
                base *= XR_ADV_StandardTexture(material, 0u, FragUv0Uv1.xy, uv0Dx, uv0Dy, vec4(1.0));
            float cutoff = clamp(uintBitsToFloat(XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_ALPHA_CUTOFF_WORD)), 0.0, 1.0);
            if (base.a < max(cutoff, 0.05))
                discard;
        }
    }

    OutColor = vec4(0.0);
#else
#if !defined(XR_SPLAT_SHADE_PER_SPLAT)
    vec3 worldPosDx = dFdx(FragWorldPosition);
    vec3 worldPosDy = dFdy(FragWorldPosition);
    vec3 normalDx = dFdx(FragWorldNormal);
    vec3 normalDy = dFdy(FragWorldNormal);
    vec3 tangentDx = dFdx(FragWorldTangent);
    vec3 tangentDy = dFdy(FragWorldTangent);
#endif

    if (r2 > MaxExponent)
        discard;

    float gaussianWeight = exp(-0.5 * r2) * smoothstep(MaxExponent, TaperStartExponent, r2);
    if (FragBaseOpacity * gaussianWeight < MinAlpha)
        discard;

    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(FragMaterialAndColor.x);
    bool isUber = XR_UBER_IsMaterial(material);
    vec3 radiance;
    float coverageAlpha;
#if defined(XR_SPLAT_SHADE_PER_SPLAT)
    // A negative alpha marks a splat that its material rejects (an alpha cutout below the cutoff).
    if (FragSplatShade.a < 0.0)
        discard;
    if (isUber)
        XR_UBER_Material = material;
    radiance = FragSplatShade.rgb;
    coverageAlpha = clamp(FragSplatShade.a, 0.0, 1.0);
#else
    XRSplatShadeInput shadeInput;
    shadeInput.worldPosition = FragWorldPosition;
    shadeInput.localPosition = FragLocalPosition;
    shadeInput.worldPositionDx = worldPosDx;
    shadeInput.worldPositionDy = worldPosDy;
    shadeInput.normal = FragWorldNormal;
    shadeInput.normalDx = normalDx;
    shadeInput.normalDy = normalDy;
    shadeInput.tangent = FragWorldTangent;
    shadeInput.tangentDx = tangentDx;
    shadeInput.tangentDy = tangentDy;
    shadeInput.tangentHandedness = FragTangentHandedness;
    shadeInput.uv0Uv1 = FragUv0Uv1;
    shadeInput.uv2Uv3 = FragUv2Uv3;
    shadeInput.uv0Dx = uv0Dx;
    shadeInput.uv0Dy = uv0Dy;
    shadeInput.uv1Dx = uv1Dx;
    shadeInput.uv1Dy = uv1Dy;
    shadeInput.uv2Dx = uv2Dx;
    shadeInput.uv2Dy = uv2Dy;
    shadeInput.uv3Dx = uv3Dx;
    shadeInput.uv3Dy = uv3Dy;
    shadeInput.materialIndex = FragMaterialAndColor.x;
    shadeInput.packedColor = FragMaterialAndColor.y;
    shadeInput.cameraPosition = InverseViewMatrix[3].xyz;
    shadeInput.pixel = uvec2(gl_FragCoord.xy);

    XRAdvancedOpaqueSampleResult shaded;
    if (!XR_SPLAT_Shade(shadeInput, shaded))
        discard;
    radiance = shaded.radiance;
    coverageAlpha = clamp(shaded.opacity, 0.0, 1.0);
#endif

    float splatWeight = clamp(FragBaseOpacity * gaussianWeight, 0.0, 1.0);
    int uberBlendMode = isUber ? UBER_I(_Mode) : (material.coverageMode == 1u ? 1 : 0);
    if (uberBlendMode == 0 || uberBlendMode == 1)
    {
        float opaqueAlpha = splatWeight;
        if (opaqueAlpha < MinAlpha)
            discard;

        OutColor = vec4(radiance * opaqueAlpha, opaqueAlpha);
    }
    else
    {
        // Surface-sampled avatar splats overlap with mean kernel sum Omega ~= 7.91 and
        // second moment Omega2 ~= 4.35. Solve per-splat opacity scale s(coverageAlpha) so
        // accumulated sheet transmittance Prod(1 - s * w_i) equals (1 - coverageAlpha),
        // and scale premultiplied radiance so accumulated sheet color equals the shaded radiance.
        const float kSplatOverlapSum = 7.91;
        const float kSplatOverlapSqSum = 4.35;
        const float kMaxOpticalDepth = kSplatOverlapSum + 0.5 * kSplatOverlapSqSum;

        if (uberBlendMode == 4)
        {
            vec3 addColor = radiance * (coverageAlpha * splatWeight / kSplatOverlapSum);
            if (max(addColor.r, max(addColor.g, addColor.b)) < (1.0 / 2048.0))
                discard;

            OutColor = vec4(addColor, 0.0);
        }
        else
        {
            // The native Uber row stores these two settings only when its schema has them.
            // Without them the splat blends straight alpha, as the native triangle path does.
            bool isPremultiplied = false;
#if defined(XR_UBER_WORD_AlphaPremultiply)
            isPremultiplied = isPremultiplied || (isUber && UBER_F(_AlphaPremultiply) > 0.5);
#endif
#if defined(XR_UBER_WORD_SceneGrabBlendMode)
            isPremultiplied = isPremultiplied || (isUber && UBER_F(_SceneGrabBlendMode) > 0.5);
#endif
            float opticalDepth = coverageAlpha >= 0.99995
                ? kMaxOpticalDepth
                : min(-log(max(1.0 - coverageAlpha, 1.0e-6)), kMaxOpticalDepth);
            float alphaScale = clamp(
                (-kSplatOverlapSum + sqrt(kSplatOverlapSum * kSplatOverlapSum + 2.0 * kSplatOverlapSqSum * opticalDepth)) / kSplatOverlapSqSum,
                0.0,
                1.0);
            float alpha = clamp(alphaScale * splatWeight, 0.0, 1.0);
            float premultScale = coverageAlpha > 1.0e-4 ? (alphaScale / coverageAlpha) : (1.0 / kSplatOverlapSum);
            vec3 premultipliedColor = isPremultiplied
                ? radiance * (premultScale * splatWeight)
                : radiance * alpha;
            if (alpha < (1.0 / 2048.0) &&
                max(premultipliedColor.r, max(premultipliedColor.g, premultipliedColor.b)) < (1.0 / 2048.0))
            {
                discard;
            }

            OutColor = vec4(premultipliedColor, alpha);
        }
    }
#endif
}
