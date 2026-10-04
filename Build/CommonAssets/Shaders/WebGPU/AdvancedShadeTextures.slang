// Finite texture/sampler bank. Every live logical pair must have an exact cohort mapping.
// Slots 0..9 are 2D, slot 10 cube, slot 11 array. The depth companion types
// slot 9 as depth/comparison and leaves all other slots unchanged (16 textures total).
#define XR_ADV_RESOURCE_FLAG_RESIDENT 1u
#define XR_ADV_RESOURCE_FLAG_FALLBACK 2u
[[vk::binding(4, 1)]] Texture2D<float4> NativeTexture0;
[[vk::binding(5, 1)]] SamplerState NativeSampler0;
[[vk::binding(6, 1)]] Texture2D<float4> NativeTexture1;
[[vk::binding(7, 1)]] SamplerState NativeSampler1;
[[vk::binding(8, 1)]] Texture2D<float4> NativeTexture2;
[[vk::binding(9, 1)]] SamplerState NativeSampler2;
[[vk::binding(10, 1)]] Texture2D<float4> NativeTexture3;
[[vk::binding(11, 1)]] SamplerState NativeSampler3;
[[vk::binding(12, 1)]] Texture2D<float4> NativeTexture4;
[[vk::binding(13, 1)]] SamplerState NativeSampler4;
[[vk::binding(14, 1)]] Texture2D<float4> NativeTexture5;
[[vk::binding(15, 1)]] SamplerState NativeSampler5;
[[vk::binding(16, 1)]] Texture2D<float4> NativeTexture6;
[[vk::binding(17, 1)]] SamplerState NativeSampler6;
[[vk::binding(18, 1)]] Texture2D<float4> NativeTexture7;
[[vk::binding(19, 1)]] SamplerState NativeSampler7;
#if defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
[[vk::binding(20, 1)]] Texture2DArray<float4> UberRasterSurface;
#else
[[vk::binding(20, 1)]] Texture2D<float4> NativeTexture8;
[[vk::binding(21, 1)]] SamplerState NativeSampler8;
#endif
#if defined(XR_ADV_DEPTH_COMPARISON_BANK)
[[vk::binding(22, 1)]] DepthTexture2D NativeTexture9;
[[vk::binding(23, 1)]] SamplerComparisonState NativeSampler9;
#else
[[vk::binding(22, 1)]] Texture2D<float4> NativeTexture9;
[[vk::binding(23, 1)]] SamplerState NativeSampler9;
#endif
[[vk::binding(24, 1)]] TextureCube<float4> NativeTexture10;
[[vk::binding(25, 1)]] SamplerState NativeSampler10;
[[vk::binding(26, 1)]] Texture2DArray<float4> NativeTexture11;
[[vk::binding(27, 1)]] SamplerState NativeSampler11;
float4 XR_ADV_TextureFallback(uint fallbackKind)
{
    if (fallbackKind == 1u || fallbackKind == 5u)
        return float4(1.0);
    if (fallbackKind == 3u)
        return float4(0.5, 0.5, 1.0, 1.0);
    if (fallbackKind == 4u)
        return float4(0.0, 0.0, 0.0, 1.0);
    return float4(0.0);
}
bool ResolveTexturePairWithSampling(XRAdvancedGpuHandle texture, XRAdvancedGpuHandle sampler, uint fallback, uint samplingKey, out XRAdvancedTextureRecord record, out XRAdvancedEncodedTextureReference encoded)
{
    record = (XRAdvancedTextureRecord)0;
    encoded = XRAdvancedEncodedTextureReference(0u, 0u, fallback, XR_ADV_RESOURCE_FLAG_FALLBACK);
    if (!XR_ADV_IsValidHandle(texture)) return false;
    uint dense = XR_ADV_ResolveVisibilityHandle(texture, VisibilityTextureLookupSegment, 0u);
    if (dense == XR_ADV_INVALID_DENSE_INDEX) { textureFailure |= 1u << 19u; return false; }
    record = XR_ADV_LoadTexture(dense);
    if (record.stableTextureId != texture.index || record.generation != texture.generation)
    { textureFailure |= 1u << 19u; return false; }
    if (!XR_ADV_IsValidHandle(sampler)) { textureFailure |= 1u << 19u; return false; }
    uint samplerDense = XR_ADV_ResolveVisibilityHandle(sampler, VisibilitySamplerLookupSegment, 0u);
    if (samplerDense == XR_ADV_INVALID_DENSE_INDEX) { textureFailure |= 1u << 19u; return false; }
    XRAdvancedSamplerRecord samplerRecord = XR_ADV_LoadSampler(samplerDense);
    if (samplerRecord.stableSamplerId != sampler.index || samplerRecord.generation != sampler.generation)
    { textureFailure |= 1u << 19u; return false; }
    for (uint i = 0u; i < TextureBindingCount(); ++i)
    {
        uint word = i * 8u;
        if (TextureBindings[word] != texture.index || TextureBindings[word+1u] != texture.generation ||
            TextureBindings[word+2u] != sampler.index || TextureBindings[word+3u] != sampler.generation ||
            TextureBindings[word+7u] != samplingKey) continue;
        uint dimension = TextureBindings[word+4u];
        uint slot = TextureBindings[word+5u];
        bool depth = (record.flags & 8u) != 0u;
        if ((TextureBindings[word+6u] != 0u) != depth) break;
#if defined(XR_ADV_DEPTH_COMPARISON_BANK)
        if (depth != (slot == 9u)) break;
#else
        if (depth) break;
#endif
        if (dimension != record.dimension || !((dimension == 1u && slot < 10u) ||
            (dimension == 3u && slot == 10u) || (dimension == 5u && slot == 11u))) break;
        encoded = XRAdvancedEncodedTextureReference(slot, uint(depth), fallback, XR_ADV_RESOURCE_FLAG_RESIDENT);
        return true;
    }
    // A valid published texture with no physical binding is never a neutral sample.
    textureFailure |= 1u << 19u;
    return false;
}
bool ResolveTexturePair(XRAdvancedGpuHandle texture, XRAdvancedGpuHandle sampler, uint fallback,
    out XRAdvancedTextureRecord record, out XRAdvancedEncodedTextureReference encoded)
{
    return ResolveTexturePairWithSampling(texture, sampler, fallback, 0u, record, encoded);
}
bool XR_ADV_TryResolveTextureReference(XRAdvancedMaterialTextureBinding binding, out XRAdvancedEncodedTextureReference encoded)
{
    XRAdvancedTextureRecord record;
    return ResolveTexturePair(binding.textureReference.handle, binding.samplerReference.handle, binding.textureReference.fallback, record, encoded);
}
bool XR_ADV_TryResolveTextureReference(XRAdvancedGpuHandle handle, uint2 textures, uint2 samplers, out XRAdvancedTextureRecord record, out XRAdvancedEncodedTextureReference encoded)
{
    record = (XRAdvancedTextureRecord)0;
    encoded = XRAdvancedEncodedTextureReference(0u, 0u, 0u, XR_ADV_RESOURCE_FLAG_FALLBACK);
    uint dense = XR_ADV_ResolveVisibilityHandle(handle, textures, 0u);
    if (dense == XR_ADV_INVALID_DENSE_INDEX)
    {
        if (XR_ADV_IsValidHandle(handle)) textureFailure |= 1u << 19u;
        return false;
    }
    XRAdvancedTextureRecord published = XR_ADV_LoadTexture(dense);
    return ResolveTexturePair(handle, published.defaultSampler, 0u, record, encoded);
}
uint2 XR_ADV_Texture2DDimensions(XRAdvancedEncodedTextureReference reference)
{
    uint width = 1u, height = 1u;
    switch (reference.payload0)
    {
        case 0u: NativeTexture0.GetDimensions(width, height); break;
        case 1u: NativeTexture1.GetDimensions(width, height); break;
        case 2u: NativeTexture2.GetDimensions(width, height); break;
        case 3u: NativeTexture3.GetDimensions(width, height); break;
        case 4u: NativeTexture4.GetDimensions(width, height); break;
        case 5u: NativeTexture5.GetDimensions(width, height); break;
        case 6u: NativeTexture6.GetDimensions(width, height); break;
        case 7u: NativeTexture7.GetDimensions(width, height); break;
#if !defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
        case 8u: NativeTexture8.GetDimensions(width, height); break;
#endif
#if !defined(XR_ADV_DEPTH_COMPARISON_BANK)
        case 9u: NativeTexture9.GetDimensions(width, height); break;
#endif
        default: textureFailure |= 1u << 19u; break;
    }
    return uint2(width, height);
}
float4 XR_ADV_SampleTexture2DGrad(XRAdvancedEncodedTextureReference reference, float2 uv, float2 dx, float2 dy)
{
    if ((reference.flags & XR_ADV_RESOURCE_FLAG_FALLBACK) != 0u) return XR_ADV_TextureFallback(reference.payload2);
    switch (reference.payload0)
    {
        case 0u: return NativeTexture0.SampleGrad(NativeSampler0, uv, dx, dy);
        case 1u: return NativeTexture1.SampleGrad(NativeSampler1, uv, dx, dy);
        case 2u: return NativeTexture2.SampleGrad(NativeSampler2, uv, dx, dy);
        case 3u: return NativeTexture3.SampleGrad(NativeSampler3, uv, dx, dy);
        case 4u: return NativeTexture4.SampleGrad(NativeSampler4, uv, dx, dy);
        case 5u: return NativeTexture5.SampleGrad(NativeSampler5, uv, dx, dy);
        case 6u: return NativeTexture6.SampleGrad(NativeSampler6, uv, dx, dy);
        case 7u: return NativeTexture7.SampleGrad(NativeSampler7, uv, dx, dy);
#if !defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
        case 8u: return NativeTexture8.SampleGrad(NativeSampler8, uv, dx, dy);
#endif
#if !defined(XR_ADV_DEPTH_COMPARISON_BANK)
        case 9u: return NativeTexture9.SampleGrad(NativeSampler9, uv, dx, dy);
#endif
    }
    textureFailure |= 1u << 19u;
    return float4(1.0, 0.0, 1.0, 1.0);
}
float4 XR_ADV_SampleTexture2DLod(XRAdvancedEncodedTextureReference reference, float2 uv, float lod)
{
    if ((reference.flags & XR_ADV_RESOURCE_FLAG_FALLBACK) != 0u) return XR_ADV_TextureFallback(reference.payload2);
    switch (reference.payload0)
    {
        case 0u: return NativeTexture0.SampleLevel(NativeSampler0, uv, lod);
        case 1u: return NativeTexture1.SampleLevel(NativeSampler1, uv, lod);
        case 2u: return NativeTexture2.SampleLevel(NativeSampler2, uv, lod);
        case 3u: return NativeTexture3.SampleLevel(NativeSampler3, uv, lod);
        case 4u: return NativeTexture4.SampleLevel(NativeSampler4, uv, lod);
        case 5u: return NativeTexture5.SampleLevel(NativeSampler5, uv, lod);
        case 6u: return NativeTexture6.SampleLevel(NativeSampler6, uv, lod);
        case 7u: return NativeTexture7.SampleLevel(NativeSampler7, uv, lod);
#if !defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
        case 8u: return NativeTexture8.SampleLevel(NativeSampler8, uv, lod);
#endif
#if !defined(XR_ADV_DEPTH_COMPARISON_BANK)
        case 9u: return NativeTexture9.SampleLevel(NativeSampler9, uv, lod);
#endif
    }
    textureFailure |= 1u << 19u;
    return float4(1.0, 0.0, 1.0, 1.0);
}
float4 XR_ADV_SampleTexture2D(XRAdvancedEncodedTextureReference reference, float2 uv) { return XR_ADV_SampleTexture2DLod(reference, uv, 0.0); }
float4 XR_ADV_SampleTextureCubeLod(XRAdvancedEncodedTextureReference reference, float3 direction, float lod)
{
    if ((reference.flags & XR_ADV_RESOURCE_FLAG_FALLBACK) != 0u) return XR_ADV_TextureFallback(reference.payload2);
    if (reference.payload0 != 10u) { textureFailure |= 1u << 19u; return float4(1.0,0.0,1.0,1.0); }
    return NativeTexture10.SampleLevel(NativeSampler10, direction, lod);
}
float4 XR_ADV_SampleTexture2DArrayLod(XRAdvancedEncodedTextureReference reference, float3 uvLayer, float lod)
{
    if ((reference.flags & XR_ADV_RESOURCE_FLAG_FALLBACK) != 0u) return XR_ADV_TextureFallback(reference.payload2);
    if (reference.payload0 != 11u) { textureFailure |= 1u << 19u; return float4(1.0,0.0,1.0,1.0); }
    return NativeTexture11.SampleLevel(NativeSampler11, uvLayer, lod);
}

// Blocker search reads the original, unfiltered depth or projected color texel.
float XR_ADV_LoadShadow2D(XRAdvancedEncodedTextureReference reference, int2 texel)
{
    switch (reference.payload0)
    {
        case 0u: return NativeTexture0.Load(int3(texel, 0)).r;
        case 1u: return NativeTexture1.Load(int3(texel, 0)).r;
        case 2u: return NativeTexture2.Load(int3(texel, 0)).r;
        case 3u: return NativeTexture3.Load(int3(texel, 0)).r;
        case 4u: return NativeTexture4.Load(int3(texel, 0)).r;
        case 5u: return NativeTexture5.Load(int3(texel, 0)).r;
        case 6u: return NativeTexture6.Load(int3(texel, 0)).r;
        case 7u: return NativeTexture7.Load(int3(texel, 0)).r;
#if !defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
        case 8u: return NativeTexture8.Load(int3(texel, 0)).r;
#endif
#if defined(XR_ADV_DEPTH_COMPARISON_BANK)
        case 9u: return NativeTexture9.Load(int3(texel, 0));
#else
        case 9u: return NativeTexture9.Load(int3(texel, 0)).r;
#endif
    }
    textureFailure |= 1u << 19u;
    return 0.0;
}

float XR_ADV_CompareShadow2D(XRAdvancedEncodedTextureReference reference, float2 uv, float receiver)
{
#if defined(XR_ADV_DEPTH_COMPARISON_BANK)
    if (reference.payload0 == 9u && reference.payload1 == 1u)
        return NativeTexture9.SampleCmpLevelZero(NativeSampler9, uv, receiver);
#endif
    if (reference.payload1 != 0u) { textureFailure |= 1u << 19u; return 0.0; }
    return float(receiver <= XR_ADV_SampleTexture2DLod(reference, uv, 0.0).r);
}
