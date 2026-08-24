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
