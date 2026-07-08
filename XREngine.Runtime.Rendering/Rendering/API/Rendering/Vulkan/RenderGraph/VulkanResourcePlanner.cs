using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Lightweight view of the render pipeline's logical resource registry that Vulkan can inspect
/// before allocating physical VkImage/VkFramebuffer objects.
/// </summary>
internal sealed class VulkanResourcePlanner
{
    private readonly Dictionary<string, TextureResourceDescriptor> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextureResourceDescriptor> _textureViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameBufferResourceDescriptor> _frameBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BufferResourceDescriptor> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private VulkanResourcePlan _plan = VulkanResourcePlan.Empty;

    public IReadOnlyDictionary<string, TextureResourceDescriptor> TextureDescriptors => _textures;
    public IReadOnlyDictionary<string, TextureResourceDescriptor> TextureViewDescriptors => _textureViews;
    public IReadOnlyDictionary<string, FrameBufferResourceDescriptor> FrameBufferDescriptors => _frameBuffers;
    public IReadOnlyDictionary<string, BufferResourceDescriptor> BufferDescriptors => _buffers;
    public VulkanResourcePlan CurrentPlan => _plan;
    public string? OutputFrameBufferName { get; private set; }

    public void Sync(RenderResourceRegistry? registry, string? outputFrameBufferName = null)
    {
        OutputFrameBufferName = outputFrameBufferName;
        _textures.Clear();
        _textureViews.Clear();
        _frameBuffers.Clear();
        _buffers.Clear();
        _plan = VulkanResourcePlan.Empty;

        if (registry is null)
            return;

        foreach ((string name, RenderTextureResource record) in registry.TextureRecords)
        {
            if (record.Descriptor.Kind == RenderPipelineResourceKind.TextureView)
                _textureViews[name] = record.Descriptor;
            else
                _textures[name] = record.Descriptor;
        }

        foreach ((string name, RenderFrameBufferResource record) in registry.FrameBufferRecords)
            _frameBuffers[name] = record.Descriptor;

        foreach ((string name, RenderBufferResource record) in registry.BufferRecords)
            _buffers[name] = record.Descriptor;

        BuildPlan();
    }

    public bool TryGetTextureDescriptor(string name, out TextureResourceDescriptor? descriptor)
        => _textures.TryGetValue(name, out descriptor)
            || _textureViews.TryGetValue(name, out descriptor);

    public bool TryGetPhysicalTextureDescriptor(string name, out TextureResourceDescriptor? descriptor)
        => _textures.TryGetValue(name, out descriptor);

    public bool TryGetTextureViewDescriptor(string name, out TextureResourceDescriptor? descriptor)
        => _textureViews.TryGetValue(name, out descriptor);

    public string ResolveImageResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        HashSet<string>? seen = null;
        string current = name;
        while (_textureViews.TryGetValue(current, out TextureResourceDescriptor? descriptor)
            && !string.IsNullOrWhiteSpace(descriptor.SourceTextureName))
        {
            seen ??= new(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(current))
                break;

            current = descriptor.SourceTextureName!;
        }

        return current;
    }

    public bool TryResolveImageResourceName(string name, out string resolvedName)
    {
        resolvedName = ResolveImageResourceName(name);
        return _textures.ContainsKey(resolvedName);
    }

    public bool TryGetFrameBufferDescriptor(string name, out FrameBufferResourceDescriptor? descriptor)
        => _frameBuffers.TryGetValue(name, out descriptor);

    public bool TryGetOutputFrameBufferDescriptor(out FrameBufferResourceDescriptor? descriptor)
    {
        descriptor = null;
        return !string.IsNullOrWhiteSpace(OutputFrameBufferName) &&
            _frameBuffers.TryGetValue(OutputFrameBufferName!, out descriptor);
    }

    public bool TryGetBufferDescriptor(string name, out BufferResourceDescriptor? descriptor)
        => _buffers.TryGetValue(name, out descriptor);

    private void BuildPlan()
    {
        if (_textures.Count == 0 && _frameBuffers.Count == 0 && _buffers.Count == 0)
        {
            _plan = VulkanResourcePlan.Empty;
            return;
        }

        List<VulkanAllocationRequest> persistent = new();
        List<VulkanAllocationRequest> transient = new();
        List<VulkanAllocationRequest> external = new();
        List<VulkanBufferAllocationRequest> persistentBuffers = new();
        List<VulkanBufferAllocationRequest> transientBuffers = new();
        List<VulkanBufferAllocationRequest> externalBuffers = new();

        foreach (TextureResourceDescriptor descriptor in _textures.Values)
        {
            var request = new VulkanAllocationRequest(descriptor);
            switch (descriptor.Lifetime)
            {
                case RenderResourceLifetime.Persistent:
                    persistent.Add(request);
                    break;
                case RenderResourceLifetime.Transient:
                    transient.Add(request);
                    break;
                case RenderResourceLifetime.External:
                    external.Add(request);
                    break;
            }
        }

        var fboPlans = new Dictionary<string, VulkanFrameBufferPlan>(_frameBuffers.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, FrameBufferResourceDescriptor descriptor) in _frameBuffers)
            fboPlans[name] = new VulkanFrameBufferPlan(descriptor);

        foreach (BufferResourceDescriptor descriptor in _buffers.Values)
        {
            var request = new VulkanBufferAllocationRequest(descriptor);
            switch (descriptor.Lifetime)
            {
                case RenderResourceLifetime.Persistent:
                    persistentBuffers.Add(request);
                    break;
                case RenderResourceLifetime.Transient:
                    transientBuffers.Add(request);
                    break;
                case RenderResourceLifetime.External:
                    externalBuffers.Add(request);
                    break;
            }
        }

        _plan = new VulkanResourcePlan(
            persistent.ToArray(),
            transient.ToArray(),
            external.ToArray(),
            persistentBuffers.ToArray(),
            transientBuffers.ToArray(),
            externalBuffers.ToArray(),
            fboPlans);
    }
}

internal readonly record struct VulkanAllocationRequest(TextureResourceDescriptor Descriptor)
{
    public string Name => Descriptor.Name;
    public RenderResourceLifetime Lifetime => Descriptor.Lifetime;
    public RenderResourceSizePolicy SizePolicy => Descriptor.SizePolicy;
    public RenderPipelineResourceUsage Usage => Descriptor.Usage;
    public ESizedInternalFormat? SizedInternalFormat => Descriptor.SizedInternalFormat;
    public EPixelInternalFormat? InternalFormat => Descriptor.InternalFormat;
    public EPixelFormat? PixelFormat => Descriptor.PixelFormat;
    public EPixelType? PixelType => Descriptor.PixelType;
    public uint Samples => Math.Max(1u, Descriptor.Samples);
    public RenderResourceMipPolicy MipPolicy
        => Descriptor.MipPolicy with { MipLevelCount = Math.Max(1u, Descriptor.MipPolicy.MipLevelCount) };
    public bool IsStereoCompatible => Descriptor.StereoCompatible;
    public VulkanTransientAttachmentPolicy TransientAttachmentPolicy => ResolveTransientAttachmentPolicy(Descriptor);
    // Temporarily disable physical image aliasing in Vulkan.
    // Aliased transient images can carry incompatible layout expectations across
    // logical resources (e.g. COLOR_ATTACHMENT_OPTIMAL vs SHADER_READ_ONLY_OPTIMAL),
    // which leads to vkQueueSubmit validation failures and device loss.
    public bool SupportsAliasing => false;
    public VulkanAliasKey AliasKey => new(
        Descriptor.SizePolicy,
        Descriptor.FormatLabel,
        Descriptor.SizedInternalFormat,
        Descriptor.InternalFormat,
        Descriptor.Usage,
        Math.Max(1u, Descriptor.Samples),
        Math.Max(1u, Descriptor.MipPolicy.MipLevelCount),
        Descriptor.ArrayLayers,
        Descriptor.StereoCompatible,
        Descriptor.RequiresStorageUsage);

    private static VulkanTransientAttachmentPolicy ResolveTransientAttachmentPolicy(TextureResourceDescriptor descriptor)
    {
        if (descriptor.Lifetime != RenderResourceLifetime.Transient)
            return VulkanTransientAttachmentPolicy.None;

        RenderPipelineResourceUsage usage = descriptor.Usage;
        bool isAttachment = (usage & (RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)) != 0;
        bool requiresPersistentShaderOrTransferAccess =
            (usage & (RenderPipelineResourceUsage.SampledTexture |
                      RenderPipelineResourceUsage.StorageImage |
                      RenderPipelineResourceUsage.TransferSource |
                      RenderPipelineResourceUsage.TransferDestination |
                      RenderPipelineResourceUsage.PresentSource)) != 0;

        return isAttachment && !requiresPersistentShaderOrTransferAccess
            ? VulkanTransientAttachmentPolicy.PreferLazilyAllocated
            : VulkanTransientAttachmentPolicy.None;
    }
}

internal enum VulkanTransientAttachmentPolicy
{
    None,
    PreferLazilyAllocated,
}

internal readonly record struct VulkanBufferAllocationRequest(BufferResourceDescriptor Descriptor)
{
    public string Name => Descriptor.Name;
    public RenderResourceLifetime Lifetime => Descriptor.Lifetime;
    public ulong SizeInBytes => Descriptor.SizeInBytes;
    public EBufferTarget Target => Descriptor.Target;
    public EBufferUsage Usage => Descriptor.Usage;
    public bool SupportsAliasing => Descriptor.SupportsAliasing;
    public VulkanBufferAliasKey AliasKey => new(Descriptor.SizeInBytes, Descriptor.Target, Descriptor.Usage);
}

internal readonly record struct VulkanBufferAliasKey(
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage Usage);

internal readonly record struct VulkanAliasKey(
    RenderResourceSizePolicy SizePolicy,
    string? FormatLabel,
    ESizedInternalFormat? SizedInternalFormat,
    EPixelInternalFormat? InternalFormat,
    RenderPipelineResourceUsage Usage,
    uint Samples,
    uint MipLevelCount,
    uint ArrayLayers,
    bool StereoCompatible,
    bool RequiresStorageUsage);

internal sealed class VulkanResourcePlan
{
    public static VulkanResourcePlan Empty { get; } = new(
        Array.Empty<VulkanAllocationRequest>(),
        Array.Empty<VulkanAllocationRequest>(),
        Array.Empty<VulkanAllocationRequest>(),
        Array.Empty<VulkanBufferAllocationRequest>(),
        Array.Empty<VulkanBufferAllocationRequest>(),
        Array.Empty<VulkanBufferAllocationRequest>(),
        new Dictionary<string, VulkanFrameBufferPlan>(StringComparer.OrdinalIgnoreCase));

    internal VulkanResourcePlan(
        IReadOnlyList<VulkanAllocationRequest> persistent,
        IReadOnlyList<VulkanAllocationRequest> transient,
        IReadOnlyList<VulkanAllocationRequest> external,
        IReadOnlyList<VulkanBufferAllocationRequest> persistentBuffers,
        IReadOnlyList<VulkanBufferAllocationRequest> transientBuffers,
        IReadOnlyList<VulkanBufferAllocationRequest> externalBuffers,
        IReadOnlyDictionary<string, VulkanFrameBufferPlan> frameBuffers)
    {
        PersistentTextures = persistent;
        TransientTextures = transient;
        ExternalTextures = external;
        PersistentBuffers = persistentBuffers;
        TransientBuffers = transientBuffers;
        ExternalBuffers = externalBuffers;
        FrameBuffers = frameBuffers;
    }

    public IReadOnlyList<VulkanAllocationRequest> PersistentTextures { get; }
    public IReadOnlyList<VulkanAllocationRequest> TransientTextures { get; }
    public IReadOnlyList<VulkanAllocationRequest> ExternalTextures { get; }
    public IReadOnlyList<VulkanBufferAllocationRequest> PersistentBuffers { get; }
    public IReadOnlyList<VulkanBufferAllocationRequest> TransientBuffers { get; }
    public IReadOnlyList<VulkanBufferAllocationRequest> ExternalBuffers { get; }
    public IReadOnlyDictionary<string, VulkanFrameBufferPlan> FrameBuffers { get; }

    public IEnumerable<VulkanAllocationRequest> AllTextures()
    {
        foreach (var req in PersistentTextures)
            yield return req;
        foreach (var req in TransientTextures)
            yield return req;
        foreach (var req in ExternalTextures)
            yield return req;
    }

    public IEnumerable<VulkanBufferAllocationRequest> AllBuffers()
    {
        foreach (var req in PersistentBuffers)
            yield return req;
        foreach (var req in TransientBuffers)
            yield return req;
        foreach (var req in ExternalBuffers)
            yield return req;
    }
}

internal readonly record struct VulkanFrameBufferPlan(FrameBufferResourceDescriptor Descriptor)
{
    public string Name => Descriptor.Name;
    public IReadOnlyList<FrameBufferAttachmentDescriptor> Attachments => Descriptor.Attachments;
}
