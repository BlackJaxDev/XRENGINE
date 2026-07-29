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
