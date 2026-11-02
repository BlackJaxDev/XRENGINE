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
        // result for its frame and descriptor generation.
        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        ulong generation = source.DescriptorGeneration;
        if (frameId != 0UL && source.TryGetFrameBindingSignature(frameId, generation, out ulong cachedSignature))
        {
            hash.Add(cachedSignature);
            return;
        }

        FrameOpSignatureHasher sourceHash = new();
        AddSourceSignature(ref sourceHash, source);
        ulong signature = sourceHash.ToHash();
        if (frameId != 0UL)
            source.StoreFrameBindingSignature(frameId, generation, signature);
        hash.Add(signature);
    }

    private static void AddSourceSignature(ref FrameOpSignatureHasher hash, IVkImageDescriptorSource source)
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
            return;
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
