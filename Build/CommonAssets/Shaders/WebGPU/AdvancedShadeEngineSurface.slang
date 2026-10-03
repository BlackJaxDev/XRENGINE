// The browser companion preserves the authored engine surface independently of
// the canonical legacy material constants and their packed texture slots.
#if !defined(XR_ADV_ENGINE_SURFACE_SCHEMA_VERSION) || XR_ADV_ENGINE_SURFACE_SCHEMA_VERSION != 2
#error Native engine surface shading requires the schema 2 cooked recipe define.
#endif
#define XR_ADV_ENGINE_SURFACE_TABLE 32u
#define XR_ADV_ENGINE_SURFACE_WORD_COUNT 76u
#define XR_ADV_ENGINE_SURFACE_SCHEMA XR_ADV_ENGINE_SURFACE_SCHEMA_VERSION
#define XR_ADV_ENGINE_SURFACE_SOURCE 1u
#define XR_ADV_ENGINE_GENERATED_SURFACE_SOURCE 7u
#define XR_ADV_ENGINE_SURFACE_COLOR 1u
#define XR_ADV_ENGINE_SURFACE_TEXTURE 2u
#define XR_ADV_ENGINE_SURFACE_TEXTURE_NORMAL 3u
#define XR_ADV_ENGINE_SURFACE_BASE_COLOR_ROLE 0u
#define XR_ADV_ENGINE_SURFACE_NORMAL_ROLE 1u
#define XR_ADV_ENGINE_SURFACE_METALLIC_ROLE 2u
#define XR_ADV_ENGINE_SURFACE_ROUGHNESS_ROLE 3u

bool XR_ADV_IsEngineSurfaceMaterial(XRAdvancedMaterialRecord material)
{
    // The final canonical header word is EAdvancedMaterialSourceContract.
    return material.reserved == XR_ADV_ENGINE_SURFACE_SOURCE ||
        material.reserved == XR_ADV_ENGINE_GENERATED_SURFACE_SOURCE;
}

float4 XR_ADV_EngineSurfaceVector(uint word)
{
    return asfloat(uint4(SceneArena[word], SceneArena[word + 1u],
        SceneArena[word + 2u], SceneArena[word + 3u]));
}

bool XR_ADV_TryLoadEngineSurfaceHeader(XRAdvancedMaterialRecord material,
    out uint word, out uint kind, out uint roleFlags)
{
    word = 0u;
    kind = 0u;
    roleFlags = 0u;
    uint arenaWordCount, arenaStride;
    SceneArena.GetDimensions(arenaWordCount, arenaStride);
    uint header = XR_ADV_ENGINE_SURFACE_TABLE * 8u;
    if (arenaWordCount < header + 8u || material.stableRowId == 0u ||
        material.generation == 0u || !XR_ADV_IsEngineSurfaceMaterial(material))
        return false;
    uint offset = SceneArena[header];
    uint count = SceneArena[header + 1u];
    if (SceneArena[header + 2u] != XR_ADV_ENGINE_SURFACE_WORD_COUNT ||
        material.stableRowId >= count || offset > arenaWordCount ||
        material.stableRowId >= (arenaWordCount - offset) / XR_ADV_ENGINE_SURFACE_WORD_COUNT)
        return false;
    word = offset + material.stableRowId * XR_ADV_ENGINE_SURFACE_WORD_COUNT;
    if (SceneArena[word] != material.generation ||
        SceneArena[word + 1u] != XR_ADV_ENGINE_SURFACE_SCHEMA)
        return false;
    kind = SceneArena[word + 2u];
    roleFlags = SceneArena[word + 3u];
    if ((roleFlags & ~15u) != 0u)
        return false;
    if (kind == XR_ADV_ENGINE_SURFACE_COLOR)
    {
        if (roleFlags != 0u) return false;
    }
    else if (kind == XR_ADV_ENGINE_SURFACE_TEXTURE)
    {
        if ((roleFlags & 3u) != 1u) return false;
    }
    else if (kind == XR_ADV_ENGINE_SURFACE_TEXTURE_NORMAL)
    {
        if ((roleFlags & 3u) != 3u) return false;
    }
    else return false;
    return true;
}

bool XR_ADV_TryLoadEngineSurface(XRAdvancedMaterialRecord material,
    out uint word, out uint kind, out uint roleFlags)
{
    if (!XR_ADV_TryLoadEngineSurfaceHeader(material, word, kind, roleFlags))
        return false;
    // Metadata is frozen for future companions; this schema admits only the
    // exact UV0, identity-transform, red-scalar and RGB-normal surface contract.
    for (uint role = 0u; role < 4u; ++role)
    {
        if ((roleFlags & (1u << role)) == 0u) continue;
        uint roleWord = word + 12u + role * 16u;
        XRAdvancedMaterialTextureBinding binding = ReadXRAdvancedMaterialTextureBinding(roleWord);
        if (!XR_ADV_IsValidHandle(binding.textureReference.handle) ||
            !XR_ADV_IsValidHandle(binding.samplerReference.handle) ||
            any(XR_ADV_EngineSurfaceVector(roleWord + 8u) != float4(1.0, 1.0, 0.0, 0.0)) ||
            asfloat(SceneArena[roleWord + 12u]) != 0.0 ||
            SceneArena[roleWord + 13u] != 0u || SceneArena[roleWord + 14u] != 0u)
            return false;
        uint decodeFlags = SceneArena[roleWord + 15u];
        if ((role == XR_ADV_ENGINE_SURFACE_BASE_COLOR_ROLE && (decodeFlags & ~1u) != 0u) ||
            (role == XR_ADV_ENGINE_SURFACE_NORMAL_ROLE && decodeFlags != 2u) ||
            (role >= XR_ADV_ENGINE_SURFACE_METALLIC_ROLE && decodeFlags != 0u))
            return false;
    }
    return XR_ADV_IsFinite(XR_ADV_EngineSurfaceVector(word + 4u)) &&
        XR_ADV_IsFinite(XR_ADV_EngineSurfaceVector(word + 8u));
}

float4 XR_ADV_SampleEngineSurfaceRole(uint word, uint role, XRAdvancedSurface surface)
{
    XRAdvancedEncodedTextureReference textureReference;
    XR_ADV_TryResolveTextureReference(
        ReadXRAdvancedMaterialTextureBinding(word + 12u + role * 16u), textureReference);
    // Authored GPU texture formats own color decoding. The frozen metadata
    // never applies another sRGB conversion to hardware-decoded samples.
    return XR_ADV_SampleTexture2DGrad(textureReference, surface.texCoord0,
        surface.texCoord0Dx, surface.texCoord0Dy);
}

float3 XR_ADV_EngineSurfaceMappedNormal(float3 sample, XRAdvancedSurface surface)
{
    // Keep the same basis guards, Y convention and outward-Z floor as
    // StandardLitTextureSampling.slang.
    sample = sample * 2.0 - 1.0;
    float3 n = normalize(surface.shadingNormal);
    if (!XR_ADV_IsFinite(n)) return float3(0.0, 0.0, 1.0);
    float3 t = surface.tangent - n * dot(n, surface.tangent);
    float3 b = surface.bitangent - n * dot(n, surface.bitangent);
    if (!XR_ADV_IsFinite(t) || !XR_ADV_IsFinite(b) || dot(t, t) < 1e-10 || dot(b, b) < 1e-10) return n;
    sample.y = -sample.y;
    if (dot(sample, sample) <= 1e-6) return n;
    sample = normalize(sample);
    sample.z = max(sample.z, 0.001);
    sample = normalize(sample);
    return normalize(normalize(t) * sample.x + normalize(b) * sample.y + n * sample.z);
}

bool XR_ADV_TryEvaluateEngineSurface(XRAdvancedMaterialRecord material, XRAdvancedSurface surface,
    out float4 base, out float4 rmse, out float3 normal, out float3 emittedRadiance)
{
    base = float4(0.0);
    rmse = float4(0.0);
    normal = surface.shadingNormal * rsqrt(max(dot(surface.shadingNormal, surface.shadingNormal), 1e-20));
    emittedRadiance = float3(0.0);
    uint word, kind, roleFlags;
    if (!XR_ADV_TryLoadEngineSurface(material, word, kind, roleFlags))
        return false;
    base = XR_ADV_EngineSurfaceVector(word + 4u);
    rmse = XR_ADV_EngineSurfaceVector(word + 8u);
    emittedRadiance = base.rgb * rmse.w;
    if (kind != XR_ADV_ENGINE_SURFACE_COLOR)
    {
        // Opaque engine texture alpha and vertex color do not modulate the
        // authored albedo/opacity, and emission is independent of this sample.
        base.rgb *= XR_ADV_SampleEngineSurfaceRole(word, XR_ADV_ENGINE_SURFACE_BASE_COLOR_ROLE, surface).rgb;
        if ((roleFlags & (1u << XR_ADV_ENGINE_SURFACE_METALLIC_ROLE)) != 0u)
            rmse.y *= XR_ADV_SampleEngineSurfaceRole(word, XR_ADV_ENGINE_SURFACE_METALLIC_ROLE, surface).r;
        if ((roleFlags & (1u << XR_ADV_ENGINE_SURFACE_ROUGHNESS_ROLE)) != 0u)
            rmse.x *= XR_ADV_SampleEngineSurfaceRole(word, XR_ADV_ENGINE_SURFACE_ROUGHNESS_ROLE, surface).r;
        if (kind == XR_ADV_ENGINE_SURFACE_TEXTURE_NORMAL)
        {
            if (!XR_ADV_SurfaceHasAttribute(surface, XR_ADV_ATTRIBUTE_TANGENT)) return false;
            normal = XR_ADV_EngineSurfaceMappedNormal(
                XR_ADV_SampleEngineSurfaceRole(word, XR_ADV_ENGINE_SURFACE_NORMAL_ROLE, surface).rgb, surface);
        }
    }
    rmse.xy = saturate(rmse.xy);
    rmse.z = max(rmse.z, 0.0);
    return true;
}

float3 XR_ADV_EvaluateEngineSurfaceDirect(float3 N, float3 V, float3 L, float3 lightRadiance,
    float3 albedo, float roughness, float metallic, float specularIntensity,
    out float3 diffuse, out float3 specular)
{
    diffuse = float3(0.0);
    specular = float3(0.0);
    float NoL = max(dot(N, L), 0.0);
    if (NoL <= 0.0) return float3(0.0);
    float3 halfVector = V + L;
    float3 H = halfVector * rsqrt(max(dot(halfVector, halfVector), 1e-20));
    float NoH = max(dot(N, H), 0.0);
    float NoV = max(dot(N, V), 0.0);
    float HoV = max(dot(H, V), 0.0);
    float a = roughness * roughness;
    float a2 = a * a;
    float denominator = NoH * NoH * (a2 - 1.0) + 1.0;
    float distribution = a2 / max(3.14159265359 * denominator * denominator, 1e-20);
    float k = (roughness + 1.0) * (roughness + 1.0) * 0.125;
    float geometry = (NoV / (NoV * (1.0 - k) + k)) * (NoL / (NoL * (1.0 - k) + k));
    float3 F0 = lerp(float3(0.04), albedo, metallic);
    float3 F = F0 + (1.0 - F0) * exp2((-5.55473 * HoV - 6.98316) * HoV);
    diffuse = (1.0 - F) * (1.0 - metallic) * albedo / 3.14159265359 * lightRadiance * NoL;
    specular = specularIntensity * distribution * geometry * F /
        (4.0 * NoV * NoL + 0.0001) * lightRadiance * NoL;
    return diffuse + specular;
}

float3 XR_ADV_EngineSurfaceIndirectFresnel(float NoV, float3 F0, float roughness, float specularIntensity)
{
    float fresnel = exp2((-5.55473 * NoV - 6.98316) * NoV);
    return (F0 + (max(float3(1.0 - roughness), F0) - F0) * fresnel) * specularIntensity;
}
