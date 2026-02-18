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
    private readonly Dictionary<string, FrameBufferResourceDescriptor> _frameBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BufferResourceDescriptor> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private VulkanResourcePlan _plan = VulkanResourcePlan.Empty;

    public IReadOnlyDictionary<string, TextureResourceDescriptor> TextureDescriptors => _textures;
    public IReadOnlyDictionary<string, FrameBufferResourceDescriptor> FrameBufferDescriptors => _frameBuffers;
    public IReadOnlyDictionary<string, BufferResourceDescriptor> BufferDescriptors => _buffers;
    public VulkanResourcePlan CurrentPlan => _plan;

    public void Sync(RenderResourceRegistry? registry)
    {
        _textures.Clear();
        _frameBuffers.Clear();
        _buffers.Clear();
        _plan = VulkanResourcePlan.Empty;

        if (registry is null)
            return;

        foreach ((string name, RenderTextureResource record) in registry.TextureRecords)
            _textures[name] = record.Descriptor;

        foreach ((string name, RenderFrameBufferResource record) in registry.FrameBufferRecords)
            _frameBuffers[name] = record.Descriptor;

        foreach ((string name, RenderBufferResource record) in registry.BufferRecords)
            _buffers[name] = record.Descriptor;

        BuildPlan();
    }

    public bool TryGetTextureDescriptor(string name, out TextureResourceDescriptor? descriptor)
        => _textures.TryGetValue(name, out descriptor);

    public bool TryGetFrameBufferDescriptor(string name, out FrameBufferResourceDescriptor? descriptor)
        => _frameBuffers.TryGetValue(name, out descriptor);

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
    public bool IsStereoCompatible => Descriptor.StereoCompatible;
    // Temporarily disable physical image aliasing in Vulkan.
    // Aliased transient images can carry incompatible layout expectations across
    // logical resources (e.g. COLOR_ATTACHMENT_OPTIMAL vs SHADER_READ_ONLY_OPTIMAL),
    // which leads to vkQueueSubmit validation failures and device loss.
    public bool SupportsAliasing => false;
    public VulkanAliasKey AliasKey => new(
        Descriptor.SizePolicy,
        Descriptor.FormatLabel,
        Descriptor.ArrayLayers,
        Descriptor.StereoCompatible);
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
    uint ArrayLayers,
    bool StereoCompatible);

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
