using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.Resources;

public sealed class RenderTextureResource
{
    public TextureResourceDescriptor Descriptor { get; private set; }
    public XRTexture? Instance { get; private set; }

    internal RenderTextureResource(TextureResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public void UpdateDescriptor(TextureResourceDescriptor descriptor)
        => Descriptor = descriptor;

    public void Bind(XRTexture texture)
    {
        if (Instance == texture)
            return;

        // Destroy synchronously so GL handle deletion happens before the new resource
        // attaches, avoiding NVIDIA driver state corruption from leaked texture/FBO bindings.
        Instance?.Destroy(true);
        Instance = texture;
    }

    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}

public sealed class RenderFrameBufferResource
{
    public FrameBufferResourceDescriptor Descriptor { get; private set; }
    public XRFrameBuffer? Instance { get; private set; }

    internal RenderFrameBufferResource(FrameBufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public void UpdateDescriptor(FrameBufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    public void Bind(XRFrameBuffer frameBuffer)
    {
        if (Instance == frameBuffer)
            return;

        Instance?.Destroy(true);
        Instance = frameBuffer;
    }

    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}

public sealed class RenderBufferResource
{
    public BufferResourceDescriptor Descriptor { get; private set; }
    public XRDataBuffer? Instance { get; private set; }

    internal RenderBufferResource(BufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public void UpdateDescriptor(BufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    public void Bind(XRDataBuffer buffer)
    {
        if (Instance == buffer)
            return;

        Instance?.Destroy(true);
        Instance = buffer;
    }

    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}

public sealed class RenderRenderBufferResource
{
    public RenderBufferResourceDescriptor Descriptor { get; private set; }
    public XRRenderBuffer? Instance { get; private set; }

    internal RenderRenderBufferResource(RenderBufferResourceDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public void UpdateDescriptor(RenderBufferResourceDescriptor descriptor)
        => Descriptor = descriptor;

    public void Bind(XRRenderBuffer renderBuffer)
    {
        if (Instance == renderBuffer)
            return;

        Instance?.Destroy(true);
        Instance = renderBuffer;
    }

    public void DestroyInstance()
    {
        Instance?.Destroy(true);
        Instance = null;
    }
}

public sealed class RenderResourceRegistry
{
    // Reads happen on the render thread (e.g. pipeline FBO creation) while
    // writes can come from worker threads (asset/pipeline registration).
    // Using ConcurrentDictionary so concurrent reader+writer access doesn't
    // raise per-frame InvalidOperationException ("non-concurrent collections").
    private readonly ConcurrentDictionary<string, RenderTextureResource> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RenderFrameBufferResource> _frameBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RenderBufferResource> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RenderRenderBufferResource> _renderBuffers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RenderTextureResource> TextureRecords => (IReadOnlyDictionary<string, RenderTextureResource>)_textures;
    public IReadOnlyDictionary<string, RenderFrameBufferResource> FrameBufferRecords => (IReadOnlyDictionary<string, RenderFrameBufferResource>)_frameBuffers;
    public IReadOnlyDictionary<string, RenderBufferResource> BufferRecords => (IReadOnlyDictionary<string, RenderBufferResource>)_buffers;
    public IReadOnlyDictionary<string, RenderRenderBufferResource> RenderBufferRecords => (IReadOnlyDictionary<string, RenderRenderBufferResource>)_renderBuffers;

    public RenderTextureResource RegisterTextureDescriptor(TextureResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        RenderTextureResource record = _textures.GetOrAdd(descriptor.Name, static (_, d) => new RenderTextureResource(d), descriptor);
        if (!ReferenceEquals(record.Descriptor, descriptor))
            record.UpdateDescriptor(descriptor);

        return record;
    }

    public RenderFrameBufferResource RegisterFrameBufferDescriptor(FrameBufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        RenderFrameBufferResource record = _frameBuffers.GetOrAdd(descriptor.Name, static (_, d) => new RenderFrameBufferResource(d), descriptor);
        if (!ReferenceEquals(record.Descriptor, descriptor))
        {
            record.UpdateDescriptor(descriptor);
        }
        else
        {
            record.UpdateDescriptor(descriptor);
        }

        return record;
    }

    public RenderBufferResource RegisterBufferDescriptor(BufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        RenderBufferResource record = _buffers.GetOrAdd(descriptor.Name, static (_, d) => new RenderBufferResource(d), descriptor);
        if (!ReferenceEquals(record.Descriptor, descriptor))
            record.UpdateDescriptor(descriptor);

        return record;
    }

    public void BindTexture(XRTexture texture, TextureResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        string name = texture.Name ?? throw new InvalidOperationException("Texture name must be set before binding to the registry.");

        descriptor ??= RenderResourceDescriptorFactory.FromTexture(texture);
        descriptor = descriptor with { Name = name };

        RenderTextureResource record = RegisterTextureDescriptor(descriptor);
        if (record.Instance is XRTexture existingTexture && !ReferenceEquals(existingTexture, texture))
            DestroyFrameBuffersReferencing(existingTexture);

        record.Bind(texture);
    }

    public void BindFrameBuffer(XRFrameBuffer frameBuffer, FrameBufferResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        string name = frameBuffer.Name ?? throw new InvalidOperationException("FrameBuffer name must be set before binding to the registry.");

        descriptor ??= RenderResourceDescriptorFactory.FromFrameBuffer(frameBuffer);
        descriptor = descriptor with { Name = name };

        RenderFrameBufferResource record = RegisterFrameBufferDescriptor(descriptor);
        record.Bind(frameBuffer);
    }

    public void BindBuffer(XRDataBuffer buffer, BufferResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        string name = buffer.AttributeName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Data buffer attribute name must be set before binding to the registry.");

        descriptor ??= RenderResourceDescriptorFactory.FromBuffer(buffer);
        descriptor = descriptor with { Name = name };

        RenderBufferResource record = RegisterBufferDescriptor(descriptor);
        record.Bind(buffer);
    }

    public RenderRenderBufferResource RegisterRenderBufferDescriptor(RenderBufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        RenderRenderBufferResource record = _renderBuffers.GetOrAdd(descriptor.Name, static (_, d) => new RenderRenderBufferResource(d), descriptor);
        if (!ReferenceEquals(record.Descriptor, descriptor))
            record.UpdateDescriptor(descriptor);

        return record;
    }

    public void BindRenderBuffer(XRRenderBuffer renderBuffer, RenderBufferResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(renderBuffer);
        string name = renderBuffer.Name ?? throw new InvalidOperationException("RenderBuffer name must be set before binding to the registry.");

        descriptor ??= RenderResourceDescriptorFactory.FromRenderBuffer(renderBuffer);
        descriptor = descriptor with { Name = name };

        RenderRenderBufferResource record = RegisterRenderBufferDescriptor(descriptor);
        record.Bind(renderBuffer);
    }

    public bool TryGetTexture(string name, [NotNullWhen(true)] out XRTexture? texture)
    {
        if (_textures.TryGetValue(name, out RenderTextureResource? record) && record.Instance is XRTexture instance)
        {
            texture = instance;
            return true;
        }

        texture = null;
        return false;
    }

    public bool TryGetFrameBuffer(string name, [NotNullWhen(true)] out XRFrameBuffer? frameBuffer)
    {
        if (_frameBuffers.TryGetValue(name, out RenderFrameBufferResource? record) && record.Instance is XRFrameBuffer instance)
        {
            frameBuffer = instance;
            return true;
        }

        frameBuffer = null;
        return false;
    }

    public bool TryGetBuffer(string name, [NotNullWhen(true)] out XRDataBuffer? buffer)
    {
        if (_buffers.TryGetValue(name, out RenderBufferResource? record) && record.Instance is XRDataBuffer instance)
        {
            buffer = instance;
            return true;
        }

        buffer = null;
        return false;
    }

    public bool TryGetRenderBuffer(string name, [NotNullWhen(true)] out XRRenderBuffer? renderBuffer)
    {
        if (_renderBuffers.TryGetValue(name, out RenderRenderBufferResource? record) && record.Instance is XRRenderBuffer instance)
        {
            renderBuffer = instance;
            return true;
        }

        renderBuffer = null;
        return false;
    }

    public IEnumerable<XRTexture> EnumerateTextureInstances()
    {
        foreach (RenderTextureResource record in _textures.Values)
            if (record.Instance is XRTexture tex)
                yield return tex;
    }

    public IEnumerable<XRFrameBuffer> EnumerateFrameBufferInstances()
    {
        foreach (RenderFrameBufferResource record in _frameBuffers.Values)
            if (record.Instance is XRFrameBuffer fbo)
                yield return fbo;
    }

    public IEnumerable<XRDataBuffer> EnumerateBufferInstances()
    {
        foreach (RenderBufferResource record in _buffers.Values)
            if (record.Instance is XRDataBuffer buffer)
                yield return buffer;
    }

    public IEnumerable<XRRenderBuffer> EnumerateRenderBufferInstances()
    {
        foreach (RenderRenderBufferResource record in _renderBuffers.Values)
            if (record.Instance is XRRenderBuffer rb)
                yield return rb;
    }

    public void RemoveTexture(string name)
    {
        if (_textures.TryRemove(name, out RenderTextureResource? record))
        {
            if (record.Instance is XRTexture texture)
                DestroyFrameBuffersReferencing(texture);

            record.DestroyInstance();
        }
    }

    public void RemoveFrameBuffer(string name)
    {
        if (_frameBuffers.TryRemove(name, out RenderFrameBufferResource? record))
            record.DestroyInstance();
    }

    public void RemoveBuffer(string name)
    {
        if (_buffers.TryRemove(name, out RenderBufferResource? record))
            record.DestroyInstance();
    }

    public void RemoveRenderBuffer(string name)
    {
        if (_renderBuffers.TryRemove(name, out RenderRenderBufferResource? record))
            record.DestroyInstance();
    }

    public void DestroyAllPhysicalResources(bool retainDescriptors = false)
    {
        foreach (RenderFrameBufferResource record in _frameBuffers.Values)
            record.DestroyInstance();
        foreach (RenderTextureResource record in _textures.Values)
            record.DestroyInstance();
        foreach (RenderBufferResource record in _buffers.Values)
            record.DestroyInstance();
        foreach (RenderRenderBufferResource record in _renderBuffers.Values)
            record.DestroyInstance();

        if (!retainDescriptors)
        {
            _textures.Clear();
            _frameBuffers.Clear();
            _buffers.Clear();
            _renderBuffers.Clear();
        }
    }

    private void DestroyFrameBuffersReferencing(XRTexture texture)
    {
        foreach (RenderFrameBufferResource record in _frameBuffers.Values)
        {
            XRFrameBuffer? frameBuffer = record.Instance;
            if (frameBuffer?.Targets is not { Length: > 0 } targets)
                continue;

            for (int i = 0; i < targets.Length; ++i)
            {
                if (!ReferenceEquals(targets[i].Target, texture))
                    continue;

                record.DestroyInstance();
                break;
            }
        }
    }
}
