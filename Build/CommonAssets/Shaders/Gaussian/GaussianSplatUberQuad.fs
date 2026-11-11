// Fragment body for Uber-shaded Gaussian avatar splats.
// Compiled after runtime injection of NativeUberMaterialSchema and AdvancedStandardMaterialShaderContract defines.

#include "Gaussian/GaussianUberAdapter.glslinc"
#include "Advanced/Shading/ShadeNativeUber.glslinc"

layout(location = 0) in vec2 FragEllipseOffset;
layout(location = 1) in vec3 FragWorldPosition;
layout(location = 2) in vec3 FragLocalPosition;
layout(location = 3) in vec3 FragWorldNormal;
layout(location = 4) in vec3 FragWorldTangent;
layout(location = 5) in vec4 FragUv0Uv1;
layout(location = 6) flat in float FragTangentHandedness;
layout(location = 7) flat in float FragBaseOpacity;
layout(location = 8) flat in uvec2 FragMaterialAndColor;

layout(location = 0) out vec4 OutColor;

const float MaxExponent = 4.84;
const float TaperStartExponent = 3.15;
const float MinAlpha = 1.0 / 64.0;

void ShadeStandardSplatMaterial(
    XRAdvancedMaterialRecord material,
    XRAdvancedSurface surface,
    XRAdvancedViewRecord view,
    inout XRAdvancedOpaqueSampleResult result)
{
    uint materialFlags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
    vec4 base = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD) * surface.color0;
    vec4 rmse = XR_ADV_StandardVector(material, XR_ADV_STANDARD_RMSE_WORD);
    if ((materialFlags & 1u) != 0u)
    {
        base *= XR_ADV_StandardTexture(
            material,
            0u,
            surface.texCoord0,
            surface.texCoord0Dx,
            surface.texCoord0Dy,
            vec4(1.0));
    }

    if (material.coverageMode == 1u)
    {
        float cutoff = clamp(uintBitsToFloat(XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_ALPHA_CUTOFF_WORD)), 0.0, 1.0);
        if (base.a < max(cutoff, 0.05))
        {
            result.diagnostics = 1u << 18u;
            return;
        }
        base.a = 1.0;
    }
    else if (material.coverageMode == 0u)
    {
        base.a = 1.0;
    }

    vec3 emittedRadiance = XR_ADV_ResolveStandardEmission(
        material,
        surface.texCoord0,
        surface.texCoord0Dx,
        surface.texCoord0Dy,
        surface.texCoord1,
        surface.texCoord1Dx,
        surface.texCoord1Dy,
        base.rgb,
        rmse.w);

    if ((materialFlags & 4u) != 0u)
    {
        rmse.xy *= XR_ADV_StandardTexture(
            material,
            2u,
            surface.texCoord0,
            surface.texCoord0Dx,
            surface.texCoord0Dy,
            vec4(1.0)).rg;
    }

    vec3 N = normalize(surface.shadingNormal);
    if ((materialFlags & 2u) != 0u)
    {
        vec3 normalTex = XR_ADV_DecodeTangentNormal(
            XR_ADV_StandardTexture(
                material,
                1u,
                surface.texCoord0,
                surface.texCoord0Dx,
                surface.texCoord0Dy,
                vec4(0.5, 0.5, 1.0, 1.0)));
        N = normalize(surface.tangent * normalTex.x + surface.bitangent * normalTex.y + N * normalTex.z);
    }

    vec3 V = normalize(view.cameraPositionAndNear.xyz - surface.worldPosition);
    float roughness = clamp(rmse.x, 0.04, 1.0);
    float metallic = clamp(rmse.y, 0.0, 1.0);

    vec3 color = emittedRadiance;
    bool unlit = (material.eligibilityFlags & 16u) != 0u;
    if (unlit)
    {
        color += base.rgb;
    }
    else
    {
        vec3 F0 = mix(vec3(0.04), base.rgb, metallic);
        vec3 F = XR_ADV_FresnelSchlick(max(dot(N, V), 0.0), F0);
        color += XR_ADV_WorldAmbient(0u) * base.rgb * (vec3(1.0) - F) * (1.0 - metallic);

        uint lightCount = min(u_Push.lightCount, uint(XR_ADV_Lights.records.length()));
        for (uint i = 0u; i < lightCount; ++i)
        {
            XRAdvancedLightRecord light = XR_ADV_LoadLight(i);
            vec3 L;
            float attenuation = 1.0;
            if (light.type == 0u)
            {
                L = normalize(-light.directionAndOuterCone.xyz);
            }
            else if (light.type == 1u || light.type == 2u)
            {
                vec3 delta = light.positionAndRadius.xyz - surface.worldPosition;
                float dist = length(delta);
                float radius = light.positionAndRadius.w;
                if (radius <= 0.0 || dist >= radius)
                    continue;
                L = delta / max(dist, 1.0e-4);
                float normDist = dist / radius;
                float fade = clamp(1.0 - pow(normDist, 4.0), 0.0, 1.0);
                attenuation = fade * fade / (dist * dist + 1.0);
                if (light.type == 2u)
                {
                    attenuation *= smoothstep(
                        light.directionAndOuterCone.w,
                        max(light.shapeAndInnerCone.w, light.directionAndOuterCone.w + 0.0001),
                        dot(-L, normalize(light.directionAndOuterCone.xyz)));
                }
            }
            else
            {
                continue;
            }

            vec3 radiance = light.colorAndIntensity.rgb * light.colorAndIntensity.w * attenuation;
            vec3 diffuse;
            vec3 specular;
            XR_ADV_EvaluatePBRDirect(N, V, L, radiance, base.rgb, roughness, metallic, diffuse, specular);
            color += diffuse + specular;
        }

        if ((u_Push.flags & 8u) != 0u)
        {
            float probeWeights[4];
            uint probeIndices[4];
            vec3 diffuseIndirect;
            vec3 specularIndirect;
            XR_ADV_EvaluateProbeIblWithSelection(
                0u,
                surface.worldPosition,
                N,
                V,
                base.rgb,
                roughness,
                metallic,
                diffuseIndirect,
                specularIndirect,
                probeWeights,
                probeIndices);
            color += diffuseIndirect + specularIndirect;
        }
    }

    result.radiance = max(color, vec3(0.0));
    result.opacity = base.a;
    result.albedoOpacity = base;
    result.normal = N;
    result.roughness = roughness;
    result.metallic = metallic;
    result.hasMaterialSurface = 1u;
}

void main()
{
    // Evaluate screen-space UV derivatives in uniform quad control flow before any discard or material branch.
    vec2 uv0Dx = dFdx(FragUv0Uv1.xy);
    vec2 uv0Dy = dFdy(FragUv0Uv1.xy);
    vec2 uv1Dx = dFdx(FragUv0Uv1.zw);
    vec2 uv1Dy = dFdy(FragUv0Uv1.zw);

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
        coords.uv2 = FragUv0Uv1.xy;
        coords.uv3 = FragUv0Uv1.zw;
        coords.uv0Dx = uv0Dx;
        coords.uv0Dy = uv0Dy;
        coords.uv1Dx = uv1Dx;
        coords.uv1Dy = uv1Dy;
        coords.uv2Dx = uv0Dx;
        coords.uv2Dy = uv0Dy;
        coords.uv3Dx = uv1Dx;
        coords.uv3Dy = uv1Dy;
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
    vec3 worldPosDx = dFdx(FragWorldPosition);
    vec3 worldPosDy = dFdy(FragWorldPosition);
    vec3 normalDx = dFdx(FragWorldNormal);
    vec3 normalDy = dFdy(FragWorldNormal);
    vec3 tangentDx = dFdx(FragWorldTangent);
    vec3 tangentDy = dFdy(FragWorldTangent);

    if (r2 > MaxExponent)
        discard;

    float gaussianWeight = exp(-0.5 * r2) * smoothstep(MaxExponent, TaperStartExponent, r2);
    if (FragBaseOpacity * gaussianWeight < MinAlpha)
        discard;

    u_Push.lightCount = uSplatLightCount;
    u_Push.flags = uSplatShadeFlags;

    shadowReason = XR_ADV_SHADOW_OK;
    shadowVisibility = 1.0;
    directDiffuse = vec3(0.0);
    directSpecular = vec3(0.0);

    vec3 cameraPosition = InverseViewMatrix[3].xyz;
    vec3 shadingNormal = dot(FragWorldNormal, FragWorldNormal) > 1.0e-10
        ? normalize(FragWorldNormal)
        : vec3(0.0, 1.0, 0.0);
    vec3 tangent = FragWorldTangent - shadingNormal * dot(FragWorldTangent, shadingNormal);
    if (dot(tangent, tangent) <= 1.0e-10)
    {
        vec3 helper = abs(shadingNormal.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        tangent = normalize(cross(helper, shadingNormal));
    }
    else
    {
        tangent = normalize(tangent);
    }
    float handedness = FragTangentHandedness >= 0.0 ? 1.0 : -1.0;
    vec3 bitangent = normalize(cross(shadingNormal, tangent)) * handedness;

    bool backFacing = dot(shadingNormal, cameraPosition - FragWorldPosition) < 0.0;

    XRAdvancedSurface surface = XR_ADV_InvalidSurface(uvec2(0u), XR_ADV_RECONSTRUCTION_OK);
    surface.materialDenseIndex = FragMaterialAndColor.x;
    surface.requiredAttributeMask =
        XR_ADV_ATTRIBUTE_POSITION |
        XR_ADV_ATTRIBUTE_NORMAL |
        XR_ADV_ATTRIBUTE_TANGENT |
        XR_ADV_ATTRIBUTE_TEXCOORD0 |
        XR_ADV_ATTRIBUTE_TEXCOORD1 |
        XR_ADV_ATTRIBUTE_COLOR0 |
        XR_ADV_ATTRIBUTE_ANALYTICAL_DERIVATIVES;
    surface.worldPosition = FragWorldPosition;
    surface.localPosition = FragLocalPosition;
    surface.worldPositionDx = worldPosDx;
    surface.worldPositionDy = worldPosDy;
    surface.shadingNormalDx = normalDx;
    surface.shadingNormalDy = normalDy;
    surface.tangentDx = tangentDx;
    surface.tangentDy = tangentDy;
    surface.geometricNormal = shadingNormal;
    surface.shadingNormal = shadingNormal;
    surface.tangent = tangent;
    surface.bitangent = bitangent;
    surface.texCoord0 = FragUv0Uv1.xy;
    surface.texCoord1 = FragUv0Uv1.zw;
    surface.texCoord0Dx = uv0Dx;
    surface.texCoord0Dy = uv0Dy;
    surface.texCoord1Dx = uv1Dx;
    surface.texCoord1Dy = uv1Dy;
    surface.color0 = unpackUnorm4x8(FragMaterialAndColor.y);
    surface.tangentHandedness = handedness;
    surface.flags =
        XR_ADV_SURFACE_VALID |
        XR_ADV_SURFACE_DERIVATIVES_VALID |
        (backFacing ? XR_ADV_SURFACE_BACK_FACING : 0u);

    XRAdvancedViewRecord view;
    view.view = transpose(ViewMatrix);
    view.cameraPositionAndNear = vec4(cameraPosition, 0.05);
    view.cameraForwardAndFar = vec4(-InverseViewMatrix[2].xyz, 500.0);
    view.renderTimeBits = floatBitsToUint(EngineTime);
    view.sourceCameraIdentityLo = 0u;
    view.sourceCameraIdentityHi = 0u;
    view.viewId = 0u;
    view.flags = 0u;

    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(FragMaterialAndColor.x);
    XRAdvancedOpaqueSampleResult shaded = XR_ADV_NeutralOpaqueSampleResult();
    bool isUber = XR_UBER_IsMaterial(material);
    if (isUber)
    {
        XR_UBER_Shade(uvec2(gl_FragCoord.xy), surface, view, material, 1.0, shaded);
    }
    else
    {
        ShadeStandardSplatMaterial(material, surface, view, shaded);
    }

    if ((shaded.diagnostics & (1u << 18u)) != 0u)
        discard;

    float coverageAlpha = clamp(shaded.opacity, 0.0, 1.0);
    if (isUber)
    {
        int uberBlendMode = UBER_I(_Mode);
        if (uberBlendMode == 0 || uberBlendMode == 1)
            coverageAlpha = 1.0;
    }

    float alpha = clamp(coverageAlpha * FragBaseOpacity * gaussianWeight, 0.0, 1.0);
    if (alpha < MinAlpha)
        discard;

    OutColor = vec4(shaded.radiance * alpha, alpha);
#endif
}
