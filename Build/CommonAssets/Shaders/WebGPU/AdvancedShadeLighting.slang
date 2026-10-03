// Canonical StandardMaterial, GGX PBR, octahedral probe IBL, shadow and decal evaluation.


bool XR_ADV_IsStandardMaterial(XRAdvancedMaterialRecord material)
{
    uint2 hash = material.materialLayoutHash;
    return material.constantWordCount >= XR_ADV_STANDARD_WORD_COUNT &&
        material.constantWordOffset <= SceneCount(11u) &&
        material.constantWordCount <= SceneCount(11u) - material.constantWordOffset &&
        (all(equal(hash, uint2(XR_ADV_STANDARD_DEFERRED_LAYOUT_LO, XR_ADV_STANDARD_DEFERRED_LAYOUT_HI))) ||
         all(equal(hash, uint2(XR_ADV_STANDARD_FORWARD_LAYOUT_LO, XR_ADV_STANDARD_FORWARD_LAYOUT_HI))) ||
         all(equal(hash, uint2(XR_ADV_STANDARD_MASKED_LAYOUT_LO, XR_ADV_STANDARD_MASKED_LAYOUT_HI))));
}

float4 XR_ADV_StandardVector(XRAdvancedMaterialRecord material, uint word)
{
    return float4(asfloat(XR_ADV_LoadMaterialConstant(material, word)),
        asfloat(XR_ADV_LoadMaterialConstant(material, word + 1u)),
        asfloat(XR_ADV_LoadMaterialConstant(material, word + 2u)),
        asfloat(XR_ADV_LoadMaterialConstant(material, word + 3u)));
}

float4 XR_ADV_StandardTexture(XRAdvancedMaterialRecord material, uint slot,
    float2 uv, float2 dx, float2 dy, float4 absent)
{
    if (slot >= material.textureReferenceCount ||
        material.textureReferenceOffset >= SceneCount(12u) ||
        slot >= SceneCount(12u) - material.textureReferenceOffset)
        return absent;
    XRAdvancedEncodedTextureReference textureReference;
    XR_ADV_TryResolveTextureReference(XR_ADV_LoadMaterialTextureBinding(material, slot), textureReference);
    return XR_ADV_SampleTexture2DGrad(textureReference, uv, dx, dy);
}

float3 XR_ADV_ResolveStandardEmission(
    XRAdvancedMaterialRecord material,
    float2 uv0,
    float2 uv0Dx,
    float2 uv0Dy,
    float2 uv1,
    float2 uv1Dx,
    float2 uv1Dy,
    float3 legacyBaseColor,
    float legacyEmission)
{
    float4 emissionColor = XR_ADV_StandardVector(material, XR_ADV_STANDARD_EMISSION_COLOR_WORD);
    if (emissionColor.a < 0.5)
        return legacyBaseColor * max(legacyEmission, 0.0);

    float strength = asfloat(XR_ADV_LoadMaterialConstant(material,
        XR_ADV_STANDARD_EMISSION_STRENGTH_WORD));
    float3 emission = emissionColor.rgb * max(strength, 0.0);
    if ((material.featureFlags & (1u << 3u)) != 0u)
    {
        float4 metadata = XR_ADV_StandardVector(material,
            XR_ADV_STANDARD_EMISSION_TEXTURE_METADATA_WORD);
        float4 scaleOffset = XR_ADV_StandardVector(material,
            XR_ADV_STANDARD_EMISSION_UV_SCALE_OFFSET_WORD);
        float rotationAngle = asfloat(XR_ADV_LoadMaterialConstant(material,
            XR_ADV_STANDARD_EMISSION_UV_ROTATION_WORD));
        bool useUv1 = metadata.x > 0.5;
        float2 uv = useUv1 ? uv1 : uv0;
        float2 dx = useUv1 ? uv1Dx : uv0Dx;
        float2 dy = useUv1 ? uv1Dy : uv0Dy;
        float sine = sin(rotationAngle);
        float cosine = cos(rotationAngle);
        float2x2 rotation = float2x2(cosine, -sine, sine, cosine);
        uv = mul(rotation, (uv * scaleOffset.xy)) + scaleOffset.zw;
        dx = mul(rotation, (dx * scaleOffset.xy));
        dy = mul(rotation, (dy * scaleOffset.xy));
        float3 sampled = XR_ADV_StandardTexture(material, 3u, uv, dx, dy, float4(0.0)).rgb;
        if (metadata.y > 0.5)
        {
            float3 safeSampled = max(sampled, float3(0.0));
            sampled = lerp(safeSampled / 12.92,
                pow((safeSampled + 0.055) / 1.055, float3(2.4)),
                step(float3(0.04045), safeSampled));
        }
        emission *= sampled;
    }
    return emission;
}

bool XR_ADV_IsProjectiveMirrorMaterial(XRAdvancedMaterialRecord material)
{
    return material.constantWordCount >= XR_ADV_PROJECTIVE_MIRROR_WORD_COUNT &&
        material.textureReferenceCount >= 3u &&
        material.constantWordOffset <= SceneCount(11u) &&
        material.constantWordCount <= SceneCount(11u) - material.constantWordOffset &&
        all(equal(material.materialLayoutHash,
            uint2(XR_ADV_PROJECTIVE_MIRROR_LAYOUT_LO, XR_ADV_PROJECTIVE_MIRROR_LAYOUT_HI)));
}

float4x4 XR_ADV_ProjectiveMirrorMatrix(XRAdvancedMaterialRecord material, uint word)
{
    // Constants are packed as System.Numerics row-major matrix words. The GLSL constructor
    // receives columns, so transpose the packed rows while materializing the logical row-major
    // matrix consumed by XR_ADV_TransformRowVector.
    return transpose(float4x4(
        float4(asfloat(XR_ADV_LoadMaterialConstant(material, word + 0u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 4u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 8u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 12u))),
        float4(asfloat(XR_ADV_LoadMaterialConstant(material, word + 1u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 5u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 9u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 13u))),
        float4(asfloat(XR_ADV_LoadMaterialConstant(material, word + 2u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 6u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 10u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 14u))),
        float4(asfloat(XR_ADV_LoadMaterialConstant(material, word + 3u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 7u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 11u)),
             asfloat(XR_ADV_LoadMaterialConstant(material, word + 15u)))));
}

bool XR_ADV_TrySampleProjectiveMirrorEntry(
    XRAdvancedMaterialRecord material,
    uint textureSlot,
    uint matrixWord,
    uint identityLoWord,
    uint identityHiWord,
    uint validWord,
    uint framebufferYDownWord,
    XRAdvancedViewRecord view,
    float3 worldPosition,
    out float3 color)
{
    color = float3(0.0);
    if (XR_ADV_LoadMaterialConstant(material, validWord) == 0u ||
        XR_ADV_LoadMaterialConstant(material, identityLoWord) != view.sourceCameraIdentityLo ||
        XR_ADV_LoadMaterialConstant(material, identityHiWord) != view.sourceCameraIdentityHi)
        return false;

    float4 clip = XR_ADV_TransformRowVector(float4(worldPosition, 1.0),
        XR_ADV_ProjectiveMirrorMatrix(material, matrixWord));
    if (!XR_ADV_IsFinite(clip) || clip.w <= 1.0e-6)
        return false;

    float2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (XR_ADV_LoadMaterialConstant(material, framebufferYDownWord) != 0u)
        uv.y = 1.0 - uv.y;
    if (!XR_ADV_IsFinite(uv) || any(lessThan(uv, float2(0.0))) || any(greaterThan(uv, float2(1.0))))
        return false;

    // Surface reconstruction currently exposes texture-coordinate derivatives, but not world
    // position derivatives. Use an explicit zero gradient, which selects the conservative base
    // mip rather than sampling undefined implicit derivatives in this compute shader.
    color = XR_ADV_StandardTexture(material, textureSlot, uv, float2(0.0), float2(0.0), float4(0.0)).rgb;
    return true;
}

bool XR_ADV_TrySampleProjectiveMirror(
    XRAdvancedMaterialRecord material,
    XRAdvancedViewRecord view,
    float3 worldPosition,
    out float3 color)
{
    return XR_ADV_TrySampleProjectiveMirrorEntry(material,
        XR_ADV_PROJECTIVE_MIRROR_VIEW0_TEXTURE_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW0_MATRIX_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW0_IDENTITY_LO_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW0_IDENTITY_HI_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW0_VALID_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW0_FRAMEBUFFER_Y_DOWN_WORD,
        view, worldPosition, color) ||
        XR_ADV_TrySampleProjectiveMirrorEntry(material,
        XR_ADV_PROJECTIVE_MIRROR_VIEW1_TEXTURE_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW1_MATRIX_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW1_IDENTITY_LO_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW1_IDENTITY_HI_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW1_VALID_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW1_FRAMEBUFFER_Y_DOWN_WORD,
        view, worldPosition, color) ||
        XR_ADV_TrySampleProjectiveMirrorEntry(material,
        XR_ADV_PROJECTIVE_MIRROR_VIEW2_TEXTURE_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW2_MATRIX_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW2_IDENTITY_LO_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW2_IDENTITY_HI_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW2_VALID_WORD,
        XR_ADV_PROJECTIVE_MIRROR_VIEW2_FRAMEBUFFER_Y_DOWN_WORD,
        view, worldPosition, color);
}


static const float PI = 3.141592653589793;

float XR_ADV_DistributionGGX(float3 N, float3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    return a2 / (PI * denom * denom + 1.0e-7);
}

float XR_ADV_GeometrySmith(float3 N, float3 V, float3 L, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;

    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);

    float ggx2 = NdotV / (NdotV * (1.0 - k) + k);
    float ggx1 = NdotL / (NdotL * (1.0 - k) + k);

    return ggx1 * ggx2;
}

float3 XR_ADV_FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// GTAO attenuates only the image-based specular lobe.  This approximation is
// monotonic in AO and preserves a fully visible reflection at AO=1.
float XR_ADV_SpecularOcclusion(float NoV, float ambientOcclusion, float roughness)
{
    float exponent = exp2(-16.0 * roughness - 1.0);
    return clamp(pow(max(NoV + ambientOcclusion, 0.0), exponent) - 1.0 + ambientOcclusion, 0.0, 1.0);
}

// Analytic split-sum approximation for a prefiltered GGX environment.
float2 XR_ADV_IntegrateSpecularBrdf(float NoV, float roughness)
{
    const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
    const float4 c1 = float4(1.0, 0.0425, 1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
    return float2(-1.04, 1.04) * a004 + r.zw;
}

float3 XR_ADV_EvaluatePBRDirect(
    float3 N,
    float3 V,
    float3 L,
    float3 lightRadiance,
    float3 albedo,
    float roughness,
    float metallic,
    out float3 directDiffuse,
    out float3 directSpecular)
{
    directDiffuse = float3(0.0);
    directSpecular = float3(0.0);
    float3 halfVector = V + L;
    float3 H = halfVector * rsqrt(max(dot(halfVector, halfVector), 0.0000001));
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0)
        return float3(0.0);

    float NdotV = max(dot(N, V), 0.0);

    float3 F0 = lerp(float3(0.04), albedo, metallic);
    float3 F = XR_ADV_FresnelSchlick(max(dot(H, V), 0.0), F0);

    float D = XR_ADV_DistributionGGX(N, H, roughness);
    float G = XR_ADV_GeometrySmith(N, V, L, roughness);

    float3 numerator = D * G * F;
    float denominator = 4.0 * NdotV * NdotL + 0.0001;
    float3 specular = numerator / denominator;

    float3 kS = F;
    float3 kD = (float3(1.0) - kS) * (1.0 - metallic);

    directDiffuse = kD * albedo / PI * lightRadiance * NdotL;
    directSpecular = specular * lightRadiance * NdotL;
    return directDiffuse + directSpecular;
}

float3 XR_ADV_EvaluatePBRDirect(float3 N, float3 V, float3 L, float3 lightRadiance,
    float3 albedo, float roughness, float metallic)
{
    float3 diffuse, specular;
    return XR_ADV_EvaluatePBRDirect(N, V, L, lightRadiance, albedo, roughness, metallic, diffuse, specular);
}


// Probe record flags mirror EAdvancedProbeRecordFlags.  A probe is never
// considered active merely because a row exists: both published texture
// references must resolve through the immutable resource publication.
#define XR_ADV_PROBE_VALID 1u
#define XR_ADV_PROBE_OCTAHEDRAL 2u
#define XR_ADV_PROBE_PARALLAX 4u
#define XR_ADV_PROBE_NORMALIZED 8u

float2 XR_ADV_OctahedralUv(float3 direction)
{
    // Probe IBL maps use the legacy X/Z/Y octahedral axis convention.
    float3 octDir = float3(direction.x, direction.z, direction.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1.0e-5);
    float2 uv = octDir.xy;
    if (octDir.z < 0.0)
    {
        float2 signDir = float2(octDir.x >= 0.0 ? 1.0 : -1.0,
            octDir.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signDir;
    }
    return uv * 0.5 + 0.5;
}

float XR_ADV_ProbeWeight(XRAdvancedProbeRecord probe, float3 worldPosition)
{
    float3 local = worldPosition - probe.positionAndRadius.xyz - probe.influenceOffsetAndShape.xyz;
    if (probe.influenceOffsetAndShape.w > 0.5)
    {
        float3 inner = max(probe.influenceInner.xyz, float3(0.0));
        float3 outer = max(probe.influenceOuter.xyz, inner + float3(1.0e-4));
        float3 normalizedDistance = clamp((abs(local) - inner) / (outer - inner), 0.0, 1.0);
        return 1.0 - smoothstep(0.0, 1.0, max(normalizedDistance.x,
            max(normalizedDistance.y, normalizedDistance.z)));
    }
    float distance = length(local);
    float innerRadius = max(probe.influenceInner.w, 0.0);
    float outerRadius = max(probe.influenceOuter.w, innerRadius + 1.0e-4);
    return 1.0 - smoothstep(innerRadius, outerRadius, distance);
}

float3 XR_ADV_ParallaxCorrectedDirection(XRAdvancedProbeRecord probe, float3 worldPosition, float3 reflection)
{
    if ((probe.flags & XR_ADV_PROBE_PARALLAX) == 0u || probe.proxyCenterAndEnable.w <= 0.0)
        return reflection;
    float3 halfExtents = max(probe.proxyHalfExtents.xyz, float3(1.0e-4));
    float4 rotation = normalize(probe.proxyRotation);
    float3 center = probe.positionAndRadius.xyz + probe.proxyCenterAndEnable.xyz;
    // Quaternion conjugate rotates world vectors into proxy local space.
    float3 qv = -rotation.xyz;
    float3 rayOrigin = worldPosition - center;
    rayOrigin += 2.0 * cross(qv, cross(qv, rayOrigin) + rotation.w * rayOrigin);
    float3 rayDirection = reflection;
    rayDirection += 2.0 * cross(qv, cross(qv, rayDirection) + rotation.w * rayDirection);
    rayDirection = normalize(rayDirection);
    float3 absDirection = abs(rayDirection);
    float3 signDirection = lerp(float3(1.0), sign(rayDirection), step(float3(1.0e-6), absDirection));
    float3 safeDirection = signDirection * max(absDirection, float3(1.0e-6));
    float3 t1 = (halfExtents - rayOrigin) / safeDirection;
    float3 t2 = (-halfExtents - rayOrigin) / safeDirection;
    float distance = min(max(t1.x, t2.x), min(max(t1.y, t2.y), max(t1.z, t2.z)));
    if (distance <= 0.0 || isnan(distance) || isinf(distance))
        return reflection;
    float3 hitLocal = rayOrigin + rayDirection * distance;
    float3 hitWorld = center + hitLocal + 2.0 * cross(rotation.xyz,
        cross(rotation.xyz, hitLocal) + rotation.w * hitLocal);
    float3 corrected = hitWorld - probe.positionAndRadius.xyz;
    return dot(corrected, corrected) > 1.0e-10 ? normalize(corrected) : reflection;
}

bool XR_ADV_TrySampleProbe(XRAdvancedProbeRecord probe, float3 worldPosition, float3 N, float3 V, float roughness,
    out float3 diffuse, out float3 specular)
{
    diffuse = float3(0.0); specular = float3(0.0);
    if ((probe.flags & (XR_ADV_PROBE_VALID | XR_ADV_PROBE_OCTAHEDRAL)) !=
        (XR_ADV_PROBE_VALID | XR_ADV_PROBE_OCTAHEDRAL)) return false;
    XRAdvancedTextureRecord irradianceRecord, prefilterRecord;
    XRAdvancedEncodedTextureReference irradianceReference, prefilterReference;
    if (!XR_ADV_TryResolveTextureReference(probe.irradiance.handle, VisibilityTextureLookupSegment, VisibilitySamplerLookupSegment,
            irradianceRecord, irradianceReference) ||
        !XR_ADV_TryResolveTextureReference(probe.prefilteredRadiance.handle, VisibilityTextureLookupSegment, VisibilitySamplerLookupSegment,
            prefilterRecord, prefilterReference) ||
        irradianceRecord.dimension != 1u || prefilterRecord.dimension != 1u) return false;
    float3 R = XR_ADV_ParallaxCorrectedDirection(probe, worldPosition, reflect(-V, N));
    float normalization = (probe.flags & XR_ADV_PROBE_NORMALIZED) != 0u ? probe.proxyHalfExtents.w : 1.0;
    diffuse = XR_ADV_SampleTexture2D(irradianceReference, XR_ADV_OctahedralUv(N)).rgb * normalization;
    float mip = roughness * max(float(prefilterRecord.mipCount) - 1.0, 0.0);
    specular = XR_ADV_SampleTexture2DLod(prefilterReference, XR_ADV_OctahedralUv(R), mip).rgb * normalization;
    return XR_ADV_IsFinite(diffuse) &&
        XR_ADV_IsFinite(specular);
}



// Values mirror EAdvancedShadowFallbackReason and are also written to the
// shading diagnostic output. Stale tiles retain their rendered projection.
#define XR_ADV_SHADOW_OK 0u
#define XR_ADV_SHADOW_MISSING 1u
#define XR_ADV_SHADOW_STALE_HANDLE 2u
#define XR_ADV_SHADOW_NOT_RESIDENT 3u
#define XR_ADV_SHADOW_UNSUPPORTED 4u
#define XR_ADV_SHADOW_OUTSIDE 5u
#define XR_ADV_SHADOW_STALE_TILE 6u

float XR_ADV_ShadowMomentBound(float2 moments, float receiver, float4 parameters)
{
    if (receiver <= moments.x) return 1.0;
    float variance = max(moments.y - moments.x * moments.x, max(parameters.x, 0.0000001));
    float delta = receiver - moments.x;
    float probability = variance / (variance + delta * delta);
    float bleed = clamp(parameters.y, 0.0, 0.999);
    return clamp((probability - bleed) / (1.0 - bleed), 0.0, 1.0);
}

float XR_ADV_CompareShadow(XRAdvancedShadowRecord record, float4 texel, float receiver)
{
    float depth = clamp(receiver, 0.0, 1.0);
    if (record.encoding == 1u)
        return XR_ADV_ShadowMomentBound(texel.xy, depth, record.momentParameters);
    if (record.encoding == 2u)
        return XR_ADV_ShadowMomentBound(texel.xy,
            exp(clamp(record.momentParameters.z, 0.0, 42.0) * depth), record.momentParameters);
    if (record.encoding == 3u)
    {
        float positive = XR_ADV_ShadowMomentBound(texel.xy,
            exp(clamp(record.momentParameters.z, 0.0, 42.0) * depth), record.momentParameters);
        float negative = XR_ADV_ShadowMomentBound(texel.zw,
            -exp(-clamp(record.momentParameters.w, 0.0, 42.0) * depth), record.momentParameters);
        return min(positive, negative);
    }
    // Point color textures encode radial depth in increasing distance even
    // when their raster depth attachment uses a reversed projection.
    bool reversed = record.type < 2u && (record.flags & 128u) != 0u;
    return reversed ? float(depth >= texel.r) : float(depth <= texel.r);
}

bool XR_ADV_ShadowAffectsView(XRAdvancedShadowRecord record, XRAdvancedViewRecord view, uint viewIndex)
{
    if (record.type == 0u && (((record.flags & 16u) != 0u) != ((view.flags & 12u) != 0u)))
        return false;
    if ((record.viewMaskLo | record.viewMaskHi) == 0u) return true;
    return viewIndex < 32u ? (record.viewMaskLo & (1u << viewIndex)) != 0u :
        viewIndex < 64u && (record.viewMaskHi & (1u << (viewIndex - 32u))) != 0u;
}

bool XR_ADV_ProjectShadow(XRAdvancedShadowRecord record, float3 position, out float3 coordinate)
{
    coordinate = float3(0.0);
    if (record.type == 3u) return record.renderedLightPositionAndFar.w > 0.0;
    float4 clip = XR_ADV_TransformRowVector(float4(position, 1.0), record.worldToShadow);
    if (clip.w <= 0.000001 || !XR_ADV_IsFinite(clip)) return false;
    float3 ndc = clip.xyz / clip.w;
    coordinate = float3(ndc.xy * 0.5 + 0.5,
        (record.flags & 32u) != 0u ? ndc.z : ndc.z * 0.5 + 0.5);
    if ((record.flags & 64u) != 0u) coordinate.y = 1.0 - coordinate.y;
    return all(greaterThanEqual(coordinate, float3(0.0))) && all(lessThanEqual(coordinate, float3(1.0)));
}

float4 XR_ADV_FetchShadow(XRAdvancedShadowRecord record, XRAdvancedTextureRecord textureRecord,
    XRAdvancedEncodedTextureReference textureReference, float2 uv, float3 direction)
{
    if (textureRecord.dimension == 3u)
        return XR_ADV_SampleTextureCubeLod(textureReference, direction, 0.0);
    if (textureRecord.dimension == 5u)
        return XR_ADV_SampleTexture2DArrayLod(textureReference, float3(uv, float(record.textureLayer)), 0.0);
    return XR_ADV_SampleTexture2DLod(textureReference, uv, 0.0);
}

#include "AdvancedShadeStandaloneShadows.slang"

float XR_ADV_SampleShadowRecord(XRAdvancedShadowRecord record, XRAdvancedSurface surface, float3 N, float3 L,
    out uint reason)
{
    reason = XR_ADV_SHADOW_OK;
    if ((record.flags & 1u) == 0u ||
        (record.lastRenderedFrameLo | record.lastRenderedFrameHi) == 0u && (record.flags & 1024u) == 0u)
    {
        reason = XR_ADV_SHADOW_NOT_RESIDENT;
        return 1.0;
    }
    if (record.encoding > 3u || record.type > 3u)
    {
        reason = XR_ADV_SHADOW_UNSUPPORTED;
        return 1.0;
    }
    XRAdvancedTextureRecord textureRecord;
    XRAdvancedEncodedTextureReference textureReference;
    if (!XR_ADV_TryResolveTextureReference(record.textureReference.handle,
        VisibilityTextureLookupSegment, VisibilitySamplerLookupSegment, textureRecord, textureReference))
    {
        reason = XR_ADV_SHADOW_NOT_RESIDENT;
        return 1.0;
    }
    if ((record.type == 3u && textureRecord.dimension != 3u) ||
        (record.type != 3u && textureRecord.dimension != 1u && textureRecord.dimension != 5u) ||
        (textureRecord.dimension == 5u && record.textureLayer >= textureRecord.depthOrLayers) ||
        textureRecord.width == 0u || textureRecord.height == 0u)
    {
        reason = XR_ADV_SHADOW_UNSUPPORTED;
        return 1.0;
    }
    if ((record.flags & 512u) != 0u)
        return XR_WEB_SampleStandaloneShadow(record, textureRecord, textureReference, surface, N, reason);
    float3 position = surface.worldPosition;
    float3 receiverPosition = position + N * max(record.depthBiasAndFilter.z, 0.0);
    float3 coordinate;
    if (!XR_ADV_ProjectShadow(record, receiverPosition, coordinate))
    {
        reason = XR_ADV_SHADOW_OUTSIDE;
        return 1.0;
    }
    float3 radialDirection = receiverPosition - record.renderedLightPositionAndFar.xyz;
    float receiver = coordinate.z;
    if (record.type >= 2u)
    {
        if (record.renderedLightPositionAndFar.w <= 0.0)
        {
            reason = XR_ADV_SHADOW_NOT_RESIDENT;
            return 1.0;
        }
        receiver = length(radialDirection) / record.renderedLightPositionAndFar.w;
    }
    else if (record.encoding != 0u)
    {
        if ((record.flags & 128u) != 0u) receiver = 1.0 - receiver;
        if ((record.flags & 256u) != 0u)
        {
            float nearZ = max(record.depthRangeAndCascade.x, 0.0001);
            float farZ = max(record.depthRangeAndCascade.y, nearZ + 0.0001);
            float linearZ = nearZ * farZ / max(farZ - receiver * (farZ - nearZ), 0.000001);
            receiver = (linearZ - nearZ) / (farZ - nearZ);
        }
    }
    float bias = lerp(max(record.depthBiasAndFilter.x, 0.0), max(record.depthBiasAndFilter.y, 0.0),
        1.0 - clamp(dot(N, L), 0.0, 1.0));
    bool reversed = record.type < 2u && record.encoding == 0u && (record.flags & 128u) != 0u;
    receiver += reversed ? bias : -bias;
    float2 texelSize = 1.0 / float2(textureRecord.width, textureRecord.height);
    float2 uv = coordinate.xy * record.uvScaleBias.xy + record.uvScaleBias.zw;
    float2 lower = record.uvScaleBias.zw + texelSize * 0.5;
    float2 upper = max(lower, record.uvScaleBias.zw + record.uvScaleBias.xy - texelSize * 0.5);
    float radius = clamp(record.depthBiasAndFilter.w, 0.0, 8.0);
    float visibility = 0.0;
    if (textureRecord.dimension == 3u)
    {
        float3 direction = normalize(radialDirection);
        float3 axis = abs(direction.z) < 0.99 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
        float3 tangent = normalize(cross(axis, direction));
        float3 bitangent = cross(direction, tangent);
        float angularRadius = 2.0 * radius / float(textureRecord.width);
        for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x)
                visibility += XR_ADV_CompareShadow(record,
                    XR_ADV_FetchShadow(record, textureRecord, textureReference, float2(0.0),
                        normalize(direction + angularRadius * (tangent * float(x) + bitangent * float(y)))), receiver);
    }
    else
    {
        for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x)
                visibility += XR_ADV_CompareShadow(record,
                    XR_ADV_FetchShadow(record, textureRecord, textureReference,
                        clamp(uv + float2(x, y) * texelSize * radius, lower, upper), radialDirection), receiver);
    }
    if ((record.flags & 4u) != 0u) reason = XR_ADV_SHADOW_STALE_TILE;
    return visibility / 9.0;
}

float XR_ADV_EvaluateShadow(XRAdvancedLightRecord light, XRAdvancedSurface surface, float3 N, float3 L,
    out uint reason)
{
    reason = XR_ADV_SHADOW_OK;
    if ((light.flags & 2u) == 0u) return 1.0;
    if (!XR_ADV_IsValidHandle(light.shadowRecord))
    {
        reason = XR_ADV_SHADOW_MISSING;
        return 1.0;
    }
    uint rootIndex = XR_ADV_ResolveHandle(light.shadowRecord, VisibilityShadowLookupSegment.x,
        VisibilityShadowLookupSegment.y, XR_ADV_DIAGNOSTIC_SHADOW);
    if (rootIndex == XR_ADV_INVALID_DENSE_INDEX || rootIndex >= SceneCount(6u))
    {
        reason = XR_ADV_SHADOW_STALE_HANDLE;
        return 1.0;
    }
    XRAdvancedShadowRecord root = XR_ADV_LoadShadow(rootIndex);
    if (!XR_ADV_HandleMatches(light.shadowRecord, root.stableShadowId, root.generation) ||
        root.cascadeOffset >= SceneCount(6u) || root.cascadeCount == 0u ||
        root.cascadeCount > SceneCount(6u) - root.cascadeOffset)
    {
        reason = XR_ADV_SHADOW_STALE_HANDLE;
        return 1.0;
    }
    XRAdvancedViewRecord view = XR_ADV_LoadView(surface.viewIndex);
    float linearDepth = -XR_ADV_TransformRowVector(float4(surface.worldPosition, 1.0), view.view).z;
    for (uint i = 0u; i < root.cascadeCount; ++i)
    {
        XRAdvancedShadowRecord record = XR_ADV_LoadShadow(root.cascadeOffset + i);
        if (!XR_ADV_ShadowAffectsView(record, view, surface.viewIndex)) continue;
        if ((record.flags & 1u) == 0u ||
            (record.lastRenderedFrameLo | record.lastRenderedFrameHi) == 0u && (record.flags & 1024u) == 0u)
        {
            reason = max(reason, XR_ADV_SHADOW_NOT_RESIDENT);
            continue;
        }
        if (record.type == 0u && (record.flags & 512u) == 0u && linearDepth > record.depthRangeAndCascade.z) continue;
        float3 unusedCoordinate;
        if (!XR_ADV_ProjectShadow(record, surface.worldPosition, unusedCoordinate)) continue;
        float visibility = XR_ADV_SampleShadowRecord(record, surface, N, L, reason);
        if (record.type != 0u || (record.flags & 512u) != 0u) return visibility;
        float blendWidth = max(record.depthRangeAndCascade.w, 0.0);
        if (blendWidth <= 0.0 || linearDepth < record.depthRangeAndCascade.z - blendWidth) return visibility;
        for (uint next = i + 1u; next < root.cascadeCount; ++next)
        {
            XRAdvancedShadowRecord adjacent = XR_ADV_LoadShadow(root.cascadeOffset + next);
            if (adjacent.type != 0u || !XR_ADV_ShadowAffectsView(adjacent, view, surface.viewIndex)) continue;
            if (!XR_ADV_ProjectShadow(adjacent, surface.worldPosition, unusedCoordinate)) continue;
            uint adjacentReason;
            float adjacentVisibility = XR_ADV_SampleShadowRecord(adjacent, surface, N, L, adjacentReason);
            reason = max(reason, adjacentReason);
            return lerp(visibility, adjacentVisibility,
                clamp((linearDepth - record.depthRangeAndCascade.z + blendWidth) / blendWidth, 0.0, 1.0));
        }
        return visibility;
    }
    if (reason == XR_ADV_SHADOW_OK) reason = XR_ADV_SHADOW_OUTSIDE;
    return 1.0;
}



#define XR_ADV_DECAL_ENABLED 1u
#define XR_ADV_DECAL_AFFECT_NORMALS 2u

void XR_ADV_ApplyDecalToSurface(
    uint decalIndex,
    inout XRAdvancedSurface surface,
    inout float3 albedo, inout float3 normal, inout float roughness, inout float metallic)
{
    if (decalIndex >= SceneCount(15u)) return;
    XRAdvancedDecalRecord decal = XR_ADV_LoadDecal(decalIndex);
    if ((decal.flags & XR_ADV_DECAL_ENABLED) == 0u) return;
    if ((decal.viewMaskLo | decal.viewMaskHi) != 0u &&
        (surface.viewIndex < 32u ? (decal.viewMaskLo & (1u << surface.viewIndex)) == 0u :
            (surface.viewIndex >= 64u || (decal.viewMaskHi & (1u << (surface.viewIndex - 32u))) == 0u))) return;
    if (decal.layerMask != 0u && (decal.layerMask & surface.layerMask) == 0u) return;

    float4 decalLocalPos = XR_ADV_TransformRowVector(float4(surface.worldPosition, 1.0), decal.worldToDecal);
    if (abs(decalLocalPos.w) < 0.00001 || !XR_ADV_IsFinite(decalLocalPos)) return;
    decalLocalPos /= decalLocalPos.w;

    // Box bounds test in decal local space.
    float3 halfExtents = decal.halfExtentsAndFade.xyz;
    if (any(lessThanEqual(halfExtents, float3(0.00001)))) return;
    if (abs(decalLocalPos.x) > halfExtents.x ||
        abs(decalLocalPos.y) > halfExtents.y ||
        abs(decalLocalPos.z) > halfExtents.z)
    {
        return;
    }

    // Normalized decal UVs in [0, 1]
    float2 decalUv = (decalLocalPos.xy / halfExtents.xy) * 0.5 + 0.5;

    // Edge falloff based on fade distance
    float fade = decal.halfExtentsAndFade.w;
    float edgeDist = min(
        min(halfExtents.x - abs(decalLocalPos.x), halfExtents.y - abs(decalLocalPos.y)),
        halfExtents.z - abs(decalLocalPos.z));
    float weight = clamp(edgeDist / max(fade, 0.001), 0.0, 1.0);

    uint materialDense = XR_ADV_ResolveVisibilityHandle(decal.material, VisibilityMaterialLookupSegment, XR_ADV_DIAGNOSTIC_MATERIAL);
    if (materialDense == XR_ADV_INVALID_DENSE_INDEX || materialDense >= SceneCount(3u)) return;
    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(materialDense);
    if (!XR_ADV_IsStandardMaterial(material)) return;
    uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
    float4 decalBase = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD);
    if ((flags & 1u) != 0u) decalBase *= XR_ADV_StandardTexture(material, 0u, decalUv, float2(0.0), float2(0.0), float4(1.0));
    float opacity = clamp(decalBase.a * weight, 0.0, 1.0);
    XRAdvancedTextureRecord maskTexture;
    XRAdvancedEncodedTextureReference maskReference;
    if (XR_ADV_TryResolveTextureReference(decal.maskTexture.handle,
        VisibilityTextureLookupSegment, VisibilitySamplerLookupSegment, maskTexture, maskReference))
        opacity *= XR_ADV_SampleTexture2DGrad(maskReference, decalUv, float2(0.0), float2(0.0)).a;
    if (opacity <= 0.0) return;
    float4 decalRmse = XR_ADV_StandardVector(material, XR_ADV_STANDARD_RMSE_WORD);
    if ((flags & 4u) != 0u) decalRmse.xy *= XR_ADV_StandardTexture(material, 2u, decalUv, float2(0.0), float2(0.0), float4(1.0)).rg;
    albedo = lerp(albedo, decalBase.rgb, opacity);
    roughness = lerp(roughness, clamp(decalRmse.x, 0.04, 1.0), opacity);
    metallic = lerp(metallic, clamp(decalRmse.y, 0.0, 1.0), opacity);
    if ((decal.flags & XR_ADV_DECAL_AFFECT_NORMALS) != 0u && (flags & 2u) != 0u)
    {
        float3 localNormal = XR_ADV_StandardTexture(material, 1u, decalUv, float2(0.0), float2(0.0), float4(0.5,0.5,1.0,1.0)).xyz * 2.0 - 1.0;
        // For p_local = p_world * worldToDecal, the local plane normal maps
        // to world space through worldToDecal itself. This preserves the OBB
        // tangent basis under non-uniform decal transforms.
        float3 projectedNormal = normalize(mul((float3x3)decal.worldToDecal, localNormal));
        normal = normalize(lerp(normal, projectedNormal, opacity));
    }
}


bool XR_ADV_LightAffectsView(XRAdvancedLightRecord light, uint viewIndex)
{
    if ((light.flags & 1u) == 0u) return false;
    if ((light.viewMaskLo | light.viewMaskHi) == 0u) return true;
    return viewIndex < 32u ? (light.viewMaskLo & (1u << viewIndex)) != 0u :
        viewIndex < 64u && (light.viewMaskHi & (1u << (viewIndex - 32u))) != 0u;
}
struct XRAdvancedOpaqueSampleResult
{
    float3 radiance;
    float opacity;
    float2 velocity;
    float reactive;
    uint diagnostics;
    float3 emittedRadiance;
    float4 albedoOpacity;
    float3 normal;
    float4 rmse;
    float roughness;
    float metallic;
    uint unlit;
    uint hasMaterialSurface;
};

static uint shadowReason = XR_ADV_SHADOW_OK;
static float shadowVisibility = 1.0;
static float3 directDiffuse = float3(0.0);
static float3 directSpecular = float3(0.0);

XRAdvancedOpaqueSampleResult XR_ADV_NeutralOpaqueSampleResult()
{
    return XRAdvancedOpaqueSampleResult(
        float3(0.0),
        0.0,
        float2(0.0),
        0.0,
        0u,
        float3(0.0),
        float4(0.0),
        float3(0.0, 0.0, 1.0),
        float4(1.0, 0.0, 0.0, 0.0),
        1.0,
        0.0,
        0u,
        0u);
}
void XR_ADV_EvaluateProbeIbl(
    uint viewIndex,
    float3 worldPosition,
    float3 N,
    float3 V,
    float3 albedo,
    float roughness,
    float metallic,
    float specularIntensity,
    bool engineSurface,
    out float3 diffuseIndirect,
    out float3 specularIndirect)
{
    diffuseIndirect = float3(0.0);
    specularIndirect = float3(0.0);
    float weights[4] = {0.0, 0.0, 0.0, 0.0};
    uint indices[4] = {0u, 0u, 0u, 0u};
    for (uint index = 0u; index < SceneCount(13u); ++index)
    {
        XRAdvancedProbeRecord probe = XR_ADV_LoadProbe(index);
        if ((probe.flags & (XR_ADV_PROBE_VALID | XR_ADV_PROBE_OCTAHEDRAL)) !=
            (XR_ADV_PROBE_VALID | XR_ADV_PROBE_OCTAHEDRAL)) continue;
        if ((probe.viewMaskLo != 0u || probe.viewMaskHi != 0u) &&
            (viewIndex < 32u ? (probe.viewMaskLo & (1u << viewIndex)) == 0u :
                (viewIndex >= 64u || (probe.viewMaskHi & (1u << (viewIndex - 32u))) == 0u))) continue;
        float weight = XR_ADV_ProbeWeight(probe, worldPosition);
        if (weight <= 0.0) continue;
        for (uint slot = 0u; slot < 4u; ++slot)
        {
            if (weight <= weights[slot]) continue;
            for (uint shift = 3u; shift > slot; --shift)
            {
                weights[shift] = weights[shift - 1u];
                indices[shift] = indices[shift - 1u];
            }
            weights[slot] = weight;
            indices[slot] = index;
            break;
        }
    }
    float sum = 0.0;
    float3 irradiance = float3(0.0);
    float3 prefiltered = float3(0.0);
    for (uint slot = 0u; slot < 4u; ++slot)
    {
        if (weights[slot] <= 0.0) continue;
        float3 probeDiffuse, probeSpecular;
        if (!XR_ADV_TrySampleProbe(XR_ADV_LoadProbe(indices[slot]), worldPosition, N, V,
            roughness, probeDiffuse, probeSpecular)) continue;
        irradiance += probeDiffuse * weights[slot];
        prefiltered += probeSpecular * weights[slot];
        sum += weights[slot];
    }
    if (sum <= 0.0) return;
    irradiance /= sum;
    prefiltered /= sum;
    float NoV = max(dot(N, V), 0.0);
    float3 F0 = lerp(float3(0.04), albedo, metallic);
    float3 F = engineSurface
        ? XR_ADV_EngineSurfaceIndirectFresnel(NoV, F0, roughness, specularIntensity)
        : XR_ADV_FresnelSchlick(NoV, F0);
    diffuseIndirect = irradiance * albedo / PI * (float3(1.0) - F) * (1.0 - metallic);
    float2 brdf = XR_ADV_IntegrateSpecularBrdf(NoV, roughness);
    specularIndirect = prefiltered * (F * brdf.x + brdf.y);
}
float3 XR_ADV_EvaluateLight(
    uint index,
    XRAdvancedSurface surface,
    float3 N,
    float3 V,
    float3 albedo,
    float roughness,
    float metallic,
    float specularIntensity,
    bool engineSurface)
{
    XRAdvancedLightRecord light = XR_ADV_LoadLight(index);
    if (!XR_ADV_LightAffectsView(light, surface.viewIndex)) return float3(0.0);
    float3 L;
    float attenuation = 1.0;
    if (light.type == 0u) L = normalize(-light.directionAndOuterCone.xyz);
    else if (light.type == 1u || light.type == 2u)
    {
        float3 delta = light.positionAndRadius.xyz - surface.worldPosition;
        float distance = length(delta), radius = light.positionAndRadius.w;
        if (radius <= 0.0 || distance >= radius) return float3(0.0);
        L = delta / max(distance, 0.0001);
        attenuation = pow(clamp(1.0 - distance / radius, 0.0, 1.0), 2.0);
        if (light.type == 2u)
        {
            float outer = light.directionAndOuterCone.w;
            float inner = max(light.shapeAndInnerCone.w, outer + 0.0001);
            float cosine = dot(-L, normalize(light.directionAndOuterCone.xyz));
            attenuation *= smoothstep(outer, inner, cosine);
        }
    }
    else return float3(0.0);
    uint reason;
    float visibility = XR_ADV_EvaluateShadow(light, surface, N, L, reason);
    shadowReason = max(shadowReason, reason);
    shadowVisibility = min(shadowVisibility, visibility);
    float3 radiance = light.colorAndIntensity.rgb * light.colorAndIntensity.w * attenuation * visibility;
    float3 diffuse, specular;
    float3 result;
    if (engineSurface)
        result = XR_ADV_EvaluateEngineSurfaceDirect(N, V, L, radiance, albedo,
            roughness, metallic, specularIntensity, diffuse, specular);
    else
        result = XR_ADV_EvaluatePBRDirect(N, V, L, radiance, albedo,
            roughness, metallic, diffuse, specular);
    directDiffuse += diffuse;
    directSpecular += specular;
    return result;
}
void XR_ADV_EvaluateNativeOpaqueSample(
    uint2 pixel,
    uint viewIndex,
    uint2 identity,
    uint metadata,
    float depth,
    float2 samplePixelPosition,
    float ambientOcclusion,
    bool repair,
    uint kernelTileFlags,
    bool enforceKernel,
    out XRAdvancedOpaqueSampleResult result)
{
    result = XR_ADV_NeutralOpaqueSampleResult();

    // These are invocation globals because the light evaluator updates them.
    // A multisample driver calls this function repeatedly in one invocation.
    shadowReason = XR_ADV_SHADOW_OK;
    shadowVisibility = 1.0;
    standaloneShadowHelpersReady = false;
    directDiffuse = float3(0.0);
    directSpecular = float3(0.0);

    uint materialDense, kernelDense;
    bool metadataMatchesView = ((metadata >> XR_ADV_VIS_VIEW_SHIFT) & 0xFFu) == viewIndex;
    bool resolved = XR_ADV_TryResolvePixelMaterial(identity, metadata, materialDense, kernelDense);
    if (enforceKernel && !repair && resolved && kernelDense != u_Push.kernelIndex)
        return;
    if (enforceKernel && !repair && resolved)
    {
        bool requiresAnalyticalDerivatives =
            (XR_ADV_LoadShadingKernel(kernelDense).requiredAttributeMask &
                XR_ADV_KERNEL_REQUIRED_ANALYTICAL_DERIVATIVES) != 0u;
        bool hasAnalyticalDerivatives =
            (kernelTileFlags & XR_ADV_KERNEL_TILE_ANALYTICAL_DERIVATIVES) != 0u;
        if (requiresAnalyticalDerivatives != hasAnalyticalDerivatives)
            return;
    }

    ambientOcclusion = (!isnan(ambientOcclusion) && !isinf(ambientOcclusion))
        ? clamp(ambientOcclusion, 0.0, 1.0)
        : 1.0;
    XRAdvancedSurface surface = XR_ADV_InvalidSurface(
        identity, XR_ADV_RECONSTRUCTION_MATERIAL_NOT_RESIDENT);
    bool valid = resolved && metadataMatchesView &&
        XR_ADV_TryReconstructSurface(identity, metadata, depth, samplePixelPosition, surface);
    if (!valid || surface.viewIndex != viewIndex ||
        (repair && (u_Push.flags & XR_ADV_SHADING_REQUIRED) != 0u))
    {
        result.radiance = float3(1.0, 0.0, 1.0);
        result.opacity = 1.0;
        result.reactive = 1.0;
        uint rejection = (repair && (u_Push.flags & XR_ADV_SHADING_REQUIRED) != 0u)
            ? 1u << 18u
            : 1u << 16u;
        uint nonFiniteAttribute = surface.invalidReason != XR_ADV_RECONSTRUCTION_NON_FINITE ? 0u :
            !XR_ADV_IsFinite(surface.worldPosition) ? 1u :
            !XR_ADV_IsFinite(surface.shadingNormal) ? 2u :
            !XR_ADV_IsFinite(surface.tangent) ? 3u :
            !XR_ADV_IsFinite(surface.bitangent) ? 4u :
            !XR_ADV_IsFinite(surface.texCoord0) ? 5u :
            !XR_ADV_IsFinite(surface.texCoord0Dx) ? 6u :
            !XR_ADV_IsFinite(surface.texCoord0Dy) ? 7u : 0u;
        result.diagnostics = rejection |
            ((surface.invalidReason & 255u) << 20u) |
            (nonFiniteAttribute << 28u);
        return;
    }

    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(materialDense);
    XRAdvancedViewRecord view = XR_ADV_LoadView(viewIndex);
    bool engineSurface = XR_ADV_IsEngineSurfaceMaterial(material);
    if (!engineSurface && XR_ADV_IsProjectiveMirrorMaterial(material))
    {
        float3 mirrorColor;
        bool matchedView = XR_ADV_TrySampleProjectiveMirror(
            material, view, surface.worldPosition, mirrorColor);
        result.radiance = matchedView ? max(mirrorColor, float3(0.0)) : float3(0.0);
        result.opacity = 1.0;
        return;
    }
    if (!XR_ADV_IsStandardMaterial(material))
    {
        result.radiance = float3(1.0, 0.0, 1.0);
        result.opacity = 1.0;
        result.reactive = 1.0;
        result.diagnostics = 1u << 17u;
        return;
    }

    float4 base;
    float4 rmse;
    float3 emittedRadiance;
    float3 N;
    if (engineSurface)
    {
        if (!XR_ADV_TryEvaluateEngineSurface(material, surface, base, rmse, N, emittedRadiance))
        {
            // A missing or stale authored companion is a failed contract, not
            // permission to reinterpret the canonical legacy material slots.
            textureFailure |= 1u << 17u;
            return;
        }
    }
    else
    {
        uint materialFlags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
        base = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD) * surface.color0;
        rmse = XR_ADV_StandardVector(material, XR_ADV_STANDARD_RMSE_WORD);
        if ((materialFlags & 1u) != 0u)
            base *= XR_ADV_StandardTexture(material, 0u, surface.texCoord0,
                surface.texCoord0Dx, surface.texCoord0Dy, float4(1.0));
        emittedRadiance = XR_ADV_ResolveStandardEmission(material,
            surface.texCoord0, surface.texCoord0Dx, surface.texCoord0Dy,
            surface.texCoord1, surface.texCoord1Dx, surface.texCoord1Dy,
            base.rgb, rmse.w);
        if ((materialFlags & 4u) != 0u)
            rmse.xy *= XR_ADV_StandardTexture(material, 2u, surface.texCoord0,
                surface.texCoord0Dx, surface.texCoord0Dy, float4(1.0)).rg;
        N = normalize(surface.shadingNormal);
        if ((materialFlags & 2u) != 0u &&
            XR_ADV_SurfaceHasAttribute(surface, XR_ADV_ATTRIBUTE_TANGENT))
        {
            float3 normalTex = XR_ADV_StandardTexture(material, 1u, surface.texCoord0,
                surface.texCoord0Dx, surface.texCoord0Dy,
                float4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
            N = normalize(surface.tangent * normalTex.x +
                surface.bitangent * normalTex.y + N * normalTex.z);
        }
    }
    float3 V = normalize(view.cameraPositionAndNear.xyz - surface.worldPosition);
    float roughness = clamp(rmse.x, engineSurface ? 0.0 : 0.04, 1.0);
    float metallic = clamp(rmse.y, 0.0, 1.0);
    float linearDepth = -XR_ADV_TransformRowVector(float4(surface.worldPosition, 1.0), view.view).z;
    // Exact canonical overflow evaluation, with the same view/layer/volume tests.
    for (uint decalIndex = 0u; decalIndex < SceneCount(15u); ++decalIndex)
        XR_ADV_ApplyDecalToSurface(decalIndex, surface, base.rgb, N, roughness, metallic);

    float3 color = emittedRadiance;
    bool unlit = (material.eligibilityFlags & 16u) != 0u;
    if (unlit) color += base.rgb;
    else
    {
        uint lightCount = min(u_Push.lightCount, SceneCount(5u));
        for (uint lightIndex = 0u; lightIndex < lightCount; ++lightIndex)
            color += XR_ADV_EvaluateLight(lightIndex, surface, N, V, base.rgb,
                roughness, metallic, rmse.z, engineSurface);
        if ((u_Push.flags & 8u) != 0u)
        {
            float3 diffuseIndirect, specularIndirect;
            XR_ADV_EvaluateProbeIbl(viewIndex, surface.worldPosition, N, V,
                base.rgb, roughness, metallic, rmse.z, engineSurface, diffuseIndirect, specularIndirect);
            color += ((u_Push.flags & 16u) != 0u
                    ? float3(0.0)
                    : diffuseIndirect * ambientOcclusion) +
                specularIndirect * XR_ADV_SpecularOcclusion(
                    max(dot(N, V), 0.0), ambientOcclusion, roughness);
        }
    }

    uint debugView = (u_Push.flags >> 8u) & 255u;
    if (debugView == 1u) color = directDiffuse;
    else if (debugView == 2u) color = directSpecular;
    else if (debugView == 3u) color = emittedRadiance;
    else if (debugView == 4u)
    {
        // No fabricated froxel occupancy: this exact all-light evaluator has no grid.
        color = float3(1.0, 0.0, 1.0);
        textureFailure |= 1u << 19u;
    }
    else if (debugView == 5u) color = float3(shadowVisibility);
    else if (debugView == 6u) color = N * 0.5 + 0.5;
    else if (debugView == 7u) color = float3(clamp(rmse.x, 0.0, 1.0));
    else if (debugView == 8u) color = float3(clamp(rmse.y, 0.0, 1.0));
    else if (debugView == 9u)
        color = shadowReason == 0u ? float3(0.0, 0.5, 0.0) :
            shadowReason == XR_ADV_SHADOW_STALE_TILE ? float3(1.0, 0.5, 0.0) :
            shadowReason == XR_ADV_SHADOW_OUTSIDE ? float3(0.0, 0.0, 0.5) :
            float3(1.0, 0.0, float(shadowReason) / 6.0);
    else if (debugView == 10u) color = float3(ambientOcclusion);

    result.radiance = engineSurface ? color : max(color, float3(0.0));
    result.opacity = base.a;
    result.velocity = surface.motionNdc;
    result.reactive = (surface.flags &
        (XR_ADV_SURFACE_REACTIVE | XR_ADV_SURFACE_MASKED_EDGE)) != 0u ? 1.0 : 0.0;
    result.diagnostics = shadowReason |
        (uint(round(clamp(shadowVisibility, 0.0, 1.0) * 255.0)) << 8u);
    result.emittedRadiance = engineSurface ? emittedRadiance : max(emittedRadiance, float3(0.0));
    result.albedoOpacity = float4(unlit ? float3(0.0) :
        engineSurface ? base.rgb : max(base.rgb, float3(0.0)), base.a);
    result.normal = N;
    result.rmse = rmse;
    result.roughness = roughness;
    result.metallic = metallic;
    result.unlit = unlit ? 1u : 0u;
    result.hasMaterialSurface = 1u;
}
