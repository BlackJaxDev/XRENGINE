using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit descriptor-source input captured by a binding producer. Command signature consumers
/// can query live descriptor generations without locating a renderer through global state.
/// </summary>
internal sealed class VulkanTextureDescriptorSignaturePlan
{
    private readonly Dictionary<XRTexture, IVkImageDescriptorSource?> _sources =
        new(ReferenceEqualityComparer.Instance);

    public void Clear() => _sources.Clear();

    public void Capture(
        VulkanWrapperLookupPort wrapperLookup,
        Dictionary<uint, XRTexture> samplers,
        Dictionary<string, XRTexture> samplersByName,
        Dictionary<uint, ProgramImageBinding> images)
    {
        _sources.Clear();
        foreach (XRTexture texture in samplers.Values)
            Capture(wrapperLookup, texture);
        foreach (XRTexture texture in samplersByName.Values)
            Capture(wrapperLookup, texture);
        foreach (ProgramImageBinding binding in images.Values)
            Capture(wrapperLookup, binding.Texture);
    }

    public void CopyFrom(VulkanTextureDescriptorSignaturePlan source)
    {
        _sources.Clear();
        foreach ((XRTexture texture, IVkImageDescriptorSource? descriptorSource) in source._sources)
            _sources.Add(texture, descriptorSource);
    }

    public void AddSignature(ref FrameOpSignatureHasher hash, XRTexture? texture)
    {
        hash.Add(texture?.GetHashCode() ?? 0);
        if (texture is null ||
            !_sources.TryGetValue(texture, out IVkImageDescriptorSource? source) ||
            source is null)
        {
            hash.Add(0UL);
            return;
        }

        // One frame hashes the same textures about 176 times per draw. The texture keeps the
        // result for its descriptor generation and a cache key: its readiness epoch when it
        // tracks one, otherwise the render frame. The high bit separates the two key kinds.
        ulong generation = source.DescriptorGeneration;
        ulong cacheKey = ResolveCacheKey(source);
        if (cacheKey != 0UL && source.TryGetFrameBindingSignature(cacheKey, generation, out ulong cachedSignature))
        {
            hash.Add(s_epochMode == EEpochMode.Witness
                ? VerifyCachedSignature(source, cachedSignature)
                : cachedSignature);
            return;
        }

        FrameOpSignatureHasher sourceHash = new();
        bool settled = AddSourceSignature(ref sourceHash, source);
        ulong signature = sourceHash.ToHash();
        // Only a ready snapshot is kept. A source that is not ready can become ready
        // later in the same frame without a generation change, for example when a
        // texture view refreshes from its viewed texture or creates its sampler.
        if (cacheKey != 0UL && settled)
            source.StoreFrameBindingSignature(cacheKey, generation, signature);
        hash.Add(signature);
    }

    private enum EEpochMode : byte
    {
        Off,
        On,
        Witness,
    }

    private const ulong EpochCacheKeyBit = 1UL << 63;
    private static readonly EEpochMode s_epochMode = ResolveEpochMode();
    private static int s_reportedWitnessMismatchCount;
    private static long s_witnessCheckCount;

    private static EEpochMode ResolveEpochMode()
        => Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDescriptorSignatureEpoch)?.Trim() switch
        {
            "1" => EEpochMode.On,
            "witness" => EEpochMode.Witness,
            _ => EEpochMode.Off,
        };

    private static ulong ResolveCacheKey(IVkImageDescriptorSource source)
    {
        if (s_epochMode != EEpochMode.Off && source.DescriptorReadinessEpoch is { } epoch)
            return EpochCacheKeyBit | (epoch & ~EpochCacheKeyBit);

        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        return frameId & ~EpochCacheKeyBit;
    }

    /// <summary>
    /// Computes the signature again and logs a mismatch. A mismatch means that some
    /// readiness input changed without a descriptor generation or readiness epoch change.
    /// </summary>
    private static ulong VerifyCachedSignature(IVkImageDescriptorSource source, ulong cachedSignature)
    {
        FrameOpSignatureHasher freshHash = new();
        _ = AddSourceSignature(ref freshHash, source);
        ulong fresh = freshHash.ToHash();
        long checks = Interlocked.Increment(ref s_witnessCheckCount);
        if (fresh == cachedSignature)
        {
            if ((checks & 0xFFFFL) == 0L)
                Debug.WriteAuxiliaryLog("descriptor-signature-witness.log", $"Witness checks={checks} mismatches={Volatile.Read(ref s_reportedWitnessMismatchCount)}");
            return fresh;
        }

        if (Interlocked.Increment(ref s_reportedWitnessMismatchCount) > 20)
            return fresh;

        Debug.WriteAuxiliaryLog(
            "descriptor-signature-witness.log",
            $"Mismatch: source={source.GetType().Name} generation={source.DescriptorGeneration} epoch={source.DescriptorReadinessEpoch} ready={source.IsDescriptorReady} cached=0x{cachedSignature:X16} fresh=0x{fresh:X16}");
        return fresh;
    }

    /// <summary>
    /// Adds the descriptor identity of <paramref name="source"/> to <paramref name="hash"/>.
    /// Returns true only when the identity came from a ready descriptor snapshot.
    /// </summary>
    private static bool AddSourceSignature(ref FrameOpSignatureHasher hash, IVkImageDescriptorSource source)
    {
        if (source.TryGetDescriptorSnapshot(null, null, "BindingSignature", false,
                out VkImageDescriptorSnapshot snapshot))
        {
            hash.Add(snapshot.IsReady);
            hash.Add(snapshot.Generation);
            hash.Add(snapshot.Image.Handle);
            hash.Add(snapshot.View.Handle);
            hash.Add(snapshot.Sampler.Handle);
            hash.Add((int)snapshot.ViewType);
            hash.Add((int)snapshot.Format);
            hash.Add((int)snapshot.Aspect);
            hash.Add((int)snapshot.Usage);
            hash.Add((int)snapshot.Samples);
            hash.Add(snapshot.MipLevels);
            hash.Add(snapshot.ArrayLayers);
            return snapshot.IsReady;
        }

        hash.Add(source.IsDescriptorReady);
        hash.Add(source.DescriptorGeneration);
        hash.Add(source.DescriptorImage.Handle);
        hash.Add(source.DescriptorView.Handle);
        hash.Add(source.DescriptorSampler.Handle);
        hash.Add((int)source.DescriptorViewType);
        hash.Add((int)source.DescriptorFormat);
        hash.Add((int)source.DescriptorAspect);
        hash.Add((int)source.DescriptorUsage);
        hash.Add((int)source.DescriptorSamples);
        hash.Add(source.DescriptorMipLevels);
        hash.Add(source.DescriptorArrayLayers);
        return false;
    }

    public ulong ComputeSignature(XRTexture? texture)
    {
        FrameOpSignatureHasher hash = new();
        AddSignature(ref hash, texture);
        return hash.ToHash();
    }

    private void Capture(VulkanWrapperLookupPort wrapperLookup, XRTexture? texture)
    {
        if (texture is null || _sources.ContainsKey(texture))
            return;

        IVkImageDescriptorSource? source =
            wrapperLookup.GetOrCreate(texture, generateNow: false) as IVkImageDescriptorSource;
        _sources.Add(texture, source);
    }
}
