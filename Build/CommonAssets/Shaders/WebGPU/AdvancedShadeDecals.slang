#if !defined(XR_ADV_AUTHORED_DECAL_SCHEMA_VERSION) || XR_ADV_AUTHORED_DECAL_SCHEMA_VERSION != 1
#error Native authored decal sampling requires schema version 1.
#endif

#define XR_ADV_DECAL_ENABLED 1u
#define XR_ADV_DECAL_AFFECT_NORMALS 2u
#define XR_ADV_DECAL_AUTHORED_ALBEDO 4u
#define XR_ADV_DECAL_UNSUPPORTED_AUTHORED 8u

void XR_ADV_ApplyDecalToSurface(uint decalIndex, inout XRAdvancedSurface surface,
    inout float3 albedo, inout float3 normal, inout float roughness, inout float metallic,
    inout float specular, inout float3 emittedRadiance)
{
    if (decalIndex >= SceneCount(15u)) return;
    XRAdvancedDecalRecord decal = XR_ADV_LoadDecal(decalIndex);
    if (XR_ADV_ResolveVisibilityHandle(decal.identity, LookupSegment(15u), XR_ADV_DIAGNOSTIC_MATERIAL) != decalIndex) return;
    if ((decal.flags & XR_ADV_DECAL_ENABLED) == 0u ||
        (decal.flags & XR_ADV_DECAL_UNSUPPORTED_AUTHORED) != 0u) return;
    if ((decal.viewMaskLo | decal.viewMaskHi) != 0u &&
        (surface.viewIndex < 32u ? (decal.viewMaskLo & (1u << surface.viewIndex)) == 0u :
            (surface.viewIndex >= 64u || (decal.viewMaskHi & (1u << (surface.viewIndex - 32u))) == 0u))) return;
    if (decal.layerMask != 0u && (decal.layerMask & surface.layerMask) == 0u) return;
    float4 local = XR_ADV_TransformRowVector(float4(surface.worldPosition, 1.0), decal.worldToDecal);
    if (abs(local.w) < 0.00001 || !XR_ADV_IsFinite(local)) return;
    local /= local.w;
    float3 halfExtents = decal.halfExtentsAndFade.xyz;
    if (any(halfExtents <= float3(0.0)) || any(abs(local.xyz) > halfExtents)) return;
    float3 dx = XR_ADV_TransformRowVector(float4(surface.worldPositionDx, 0.0), decal.worldToDecal).xyz;
    float3 dy = XR_ADV_TransformRowVector(float4(surface.worldPositionDy, 0.0), decal.worldToDecal).xyz;
    bool authoredAlbedo = (decal.flags & XR_ADV_DECAL_AUTHORED_ALBEDO) != 0u;
    // DeferredDecals modifies the original GBuffer, before forward receivers.
    if (authoredAlbedo && surface.authoredRenderPass != 1u) return;
    float2 uv = authoredAlbedo ? local.xz / halfExtents.xz : local.xy / halfExtents.xy;
    float2 uvDx = (authoredAlbedo ? dx.xz / halfExtents.xz : dx.xy / halfExtents.xy) * 0.5;
    float2 uvDy = (authoredAlbedo ? dy.xz / halfExtents.xz : dy.xy / halfExtents.xy) * 0.5;
    uv = uv * 0.5 + 0.5;
    XRAdvancedTextureRecord maskTexture;
    XRAdvancedEncodedTextureReference maskReference;
    bool hasMask = XR_ADV_TryResolveTextureReference(decal.maskTexture.handle,
        VisibilityTextureLookupSegment, VisibilitySamplerLookupSegment, maskTexture, maskReference);
    if (authoredAlbedo)
    {
        // Exact DeferredDecal.fs contract: inclusive XZ box, texture-alpha RGB
        // blend; the desktop's computed depth intensity never affects its output.
        // The receiver alpha, normal, RMSI and emission remain owned by receiver.
        if (hasMask)
        {
            float4 projected = XR_ADV_SampleTexture2DGrad(maskReference, uv, uvDx, uvDy);
            albedo = lerp(albedo, projected.rgb, projected.a);
        }
        return;
    }
    float edge = min(min(halfExtents.x - abs(local.x), halfExtents.y - abs(local.y)), halfExtents.z - abs(local.z));
    float weight = saturate(edge / max(decal.halfExtentsAndFade.w, 0.001));
    uint dense = XR_ADV_ResolveVisibilityHandle(decal.material, VisibilityMaterialLookupSegment, XR_ADV_DIAGNOSTIC_MATERIAL);
    if (dense == XR_ADV_INVALID_DENSE_INDEX || dense >= SceneCount(3u)) return;
    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(dense);
    if (!XR_ADV_IsStandardMaterial(material)) return;
    float4 base, rmse;
    float3 projectedNormal = normal, emission = float3(0.0);
    bool engine = XR_ADV_IsEngineSurfaceMaterial(material);
    bool hasProjectedNormal = false;
    if (engine)
    {
        uint word, kind, roleFlags;
        if (!XR_ADV_TryLoadEngineSurfaceHeader(material, word, kind, roleFlags)) return;
        hasProjectedNormal = (roleFlags & (1u << XR_ADV_ENGINE_SURFACE_NORMAL_ROLE)) != 0u;
        XRAdvancedSurface projected = surface;
        projected.texCoord0 = uv; projected.texCoord0Dx = uvDx; projected.texCoord0Dy = uvDy;
        float3 c0 = float3(decal.worldToDecal[0][0], decal.worldToDecal[1][0], decal.worldToDecal[2][0]);
        float3 c1 = float3(decal.worldToDecal[0][1], decal.worldToDecal[1][1], decal.worldToDecal[2][1]);
        float3 c2 = float3(decal.worldToDecal[0][2], decal.worldToDecal[1][2], decal.worldToDecal[2][2]);
        float determinant = dot(c0, cross(c1, c2));
        if (abs(determinant) < 1e-20) return;
        projected.shadingNormal = normalize(c2);
        projected.tangent = normalize(cross(c1, c2) / determinant);
        projected.bitangent = normalize(cross(c2, c0) / determinant);
        projected.requiredAttributeMask |= XR_ADV_ATTRIBUTE_TANGENT;
        if (!XR_ADV_TryEvaluateEngineSurface(material, projected, base, rmse, projectedNormal, emission)) return;
    }
    else
    {
        uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
        hasProjectedNormal = (flags & 2u) != 0u;
        base = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD);
        if ((flags & 1u) != 0u) base *= XR_ADV_StandardTexture(material, 0u, uv, uvDx, uvDy, float4(1.0));
        rmse = XR_ADV_StandardVector(material, XR_ADV_STANDARD_RMSE_WORD);
        if ((flags & 4u) != 0u) rmse.xy *= XR_ADV_StandardTexture(material, 2u, uv, uvDx, uvDy, float4(1.0)).rg;
        if ((flags & 2u) != 0u)
        {
            float3 localNormal = XR_ADV_StandardTexture(material, 1u, uv, uvDx, uvDy, float4(0.5,0.5,1.0,1.0)).xyz * 2.0 - 1.0;
            projectedNormal = normalize(mul((float3x3)decal.worldToDecal, localNormal));
        }
    }
    float opacity = saturate(base.a * weight);
    if (hasMask) opacity *= XR_ADV_SampleTexture2DGrad(maskReference, uv, uvDx, uvDy).a;
    albedo = lerp(albedo, base.rgb, opacity);
    roughness = lerp(roughness, clamp(rmse.x, engine ? 0.0 : 0.04, 1.0), opacity);
    metallic = lerp(metallic, saturate(rmse.y), opacity);
    if (engine)
    {
        specular = lerp(specular, rmse.z, opacity);
        emittedRadiance = lerp(emittedRadiance, emission, opacity);
    }
    if (hasProjectedNormal && (decal.flags & XR_ADV_DECAL_AFFECT_NORMALS) != 0u)
        normal = normalize(lerp(normal, projectedNormal, opacity));
}

void XR_ADV_ApplySelectedDecals(inout XRAdvancedSurface surface,
    inout float3 albedo, inout float3 normal, inout float roughness, inout float metallic,
    inout float specular, inout float3 emittedRadiance)
{
    // Generic material decals retain their distinct XY material contract.
    for (uint index = 0u; index < SceneCount(15u); ++index)
        if ((XR_ADV_LoadDecal(index).flags & (XR_ADV_DECAL_AUTHORED_ALBEDO | XR_ADV_DECAL_UNSUPPORTED_AUTHORED)) == 0u)
            XR_ADV_ApplyDecalToSurface(index, surface, albedo, normal, roughness, metallic, specular, emittedRadiance);
    if ((Parameters.flags & 16u) == 0u) return;
    uint words, stride;
    TextureBindings.GetDimensions(words, stride);
    uint offset = Parameters.reserved0, count = Parameters.reserved1;
    if (offset > words || count > words - offset) { textureFailure |= 1u << 17u; return; }
    for (uint index = 0u; index < count; ++index)
        XR_ADV_ApplyDecalToSurface(TextureBindings[offset + index], surface, albedo, normal, roughness, metallic, specular, emittedRadiance);
}
