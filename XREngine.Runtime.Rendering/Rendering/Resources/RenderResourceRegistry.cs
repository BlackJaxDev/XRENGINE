using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Thread-tolerant registry of render resource descriptors and their live backing objects.
/// </summary>
/// <remarks>
/// The registry is the bridge between declarative render pipeline resources and concrete engine
/// objects. Descriptors are used by planners and command-buffer invalidation. Instances are the
/// live resources used by the renderer after the descriptors have been materialized.
/// </remarks>
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

    // Descriptor signatures are computed lazily because they sort the descriptor names included in
    // the cache key. The revision fields make the expensive hash stable for readers until a
    // descriptor set changes.
    private readonly object _descriptorSignatureLock = new();
    private int _descriptorRevision;
    private int _cachedDescriptorSignature;
    private int _cachedDescriptorSignatureRevision = -1;

    /// <summary>
    /// Snapshot-compatible view of all texture records keyed by logical resource name.
    /// </summary>
    public IReadOnlyDictionary<string, RenderTextureResource> TextureRecords => (IReadOnlyDictionary<string, RenderTextureResource>)_textures;

    /// <summary>
    /// Snapshot-compatible view of all framebuffer records keyed by logical resource name.
    /// </summary>
    public IReadOnlyDictionary<string, RenderFrameBufferResource> FrameBufferRecords => (IReadOnlyDictionary<string, RenderFrameBufferResource>)_frameBuffers;

    /// <summary>
    /// Snapshot-compatible view of all data-buffer records keyed by logical resource name.
    /// </summary>
    public IReadOnlyDictionary<string, RenderBufferResource> BufferRecords => (IReadOnlyDictionary<string, RenderBufferResource>)_buffers;

    /// <summary>
    /// Snapshot-compatible view of all renderbuffer records keyed by logical resource name.
    /// </summary>
    public IReadOnlyDictionary<string, RenderRenderBufferResource> RenderBufferRecords => (IReadOnlyDictionary<string, RenderRenderBufferResource>)_renderBuffers;

    /// <summary>
    /// Monotonic counter that changes whenever the registered descriptor set changes materially.
    /// </summary>
    public int DescriptorRevision => Volatile.Read(ref _descriptorRevision);

    /// <summary>
    /// Stable, order-independent signature for the descriptor set at the current revision.
    /// </summary>
    /// <remarks>
    /// Vulkan command-buffer reuse uses this value to detect resource-planning changes without
    /// comparing every descriptor every frame.
    /// </remarks>
    public int DescriptorSignature
    {
        get
        {
            int revision = DescriptorRevision;

            // Fast path: most frames only read the already-computed signature for the current revision.
            if (Volatile.Read(ref _cachedDescriptorSignatureRevision) == revision)
                return Volatile.Read(ref _cachedDescriptorSignature);

            // Slow path: serialize signature recomputation so multiple render/worker reads do not all
            // pay for the same sorted descriptor walk after one registry mutation.
            lock (_descriptorSignatureLock)
            {
                revision = DescriptorRevision;
                if (_cachedDescriptorSignatureRevision == revision)
                    return _cachedDescriptorSignature;

                int signature = ComputeDescriptorSignature();
                Volatile.Write(ref _cachedDescriptorSignature, signature);
                Volatile.Write(ref _cachedDescriptorSignatureRevision, revision);
                return signature;
            }
        }
    }

    /// <summary>
    /// Registers or updates a texture descriptor and returns its registry record.
    /// </summary>
    /// <remarks>
    /// Names are case-insensitive. Equivalent descriptor updates do not advance DescriptorRevision.
    /// </remarks>
    public RenderTextureResource RegisterTextureDescriptor(TextureResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_textures.TryGetValue(descriptor.Name, out RenderTextureResource? record))
        {
            UpdateTextureDescriptorIfChanged(record, descriptor);
            return record;
        }

        RenderTextureResource newRecord = new(descriptor);

        // GetOrAdd closes the race where another thread registers the same logical name after the
        // optimistic TryGetValue above. Only the winning insert increments the descriptor revision.
        record = _textures.GetOrAdd(descriptor.Name, newRecord);
        if (ReferenceEquals(record, newRecord))
            MarkDescriptorsChanged();
        else
            UpdateTextureDescriptorIfChanged(record, descriptor);

        return record;
    }

    /// <summary>
    /// Registers or updates a framebuffer descriptor and returns its registry record.
    /// </summary>
    public RenderFrameBufferResource RegisterFrameBufferDescriptor(FrameBufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_frameBuffers.TryGetValue(descriptor.Name, out RenderFrameBufferResource? record))
        {
            UpdateFrameBufferDescriptorIfChanged(record, descriptor);
            return record;
        }

        RenderFrameBufferResource newRecord = new(descriptor);
        record = _frameBuffers.GetOrAdd(descriptor.Name, newRecord);
        if (ReferenceEquals(record, newRecord))
            MarkDescriptorsChanged();
        else
            UpdateFrameBufferDescriptorIfChanged(record, descriptor);

        return record;
    }

    /// <summary>
    /// Registers or updates a data-buffer descriptor and returns its registry record.
    /// </summary>
    public RenderBufferResource RegisterBufferDescriptor(BufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_buffers.TryGetValue(descriptor.Name, out RenderBufferResource? record))
        {
            UpdateBufferDescriptorIfChanged(record, descriptor);
            return record;
        }

        RenderBufferResource newRecord = new(descriptor);
        record = _buffers.GetOrAdd(descriptor.Name, newRecord);
        if (ReferenceEquals(record, newRecord))
            MarkDescriptorsChanged();
        else
            UpdateBufferDescriptorIfChanged(record, descriptor);

        return record;
    }

    /// <summary>
    /// Binds a live texture to the registry, deriving a descriptor from the texture when needed.
    /// </summary>
    /// <param name="texture">Texture instance whose Name becomes the logical registry key.</param>
    /// <param name="descriptor">Optional descriptor override to register before binding.</param>
    public void BindTexture(XRTexture texture, TextureResourceDescriptor? descriptor = null, bool ownsInstance = true)
    {
        ArgumentNullException.ThrowIfNull(texture);
        string name = texture.Name ?? throw new InvalidOperationException("Texture name must be set before binding to the registry.");

        RenderTextureResource record;
        if (descriptor is null && _textures.TryGetValue(name, out RenderTextureResource? existingRecord))
        {
            record = existingRecord;
        }
        else
        {
            descriptor ??= RenderResourceDescriptorFactory.FromTexture(texture);

            // The live object name is authoritative for bind calls so a reused descriptor cannot
            // accidentally write under an unrelated logical resource key.
            descriptor = descriptor with { Name = name };
            record = RegisterTextureDescriptor(descriptor);
        }

        record.Bind(texture, ownsInstance);
    }

    /// <summary>
    /// Binds a live framebuffer to the registry, deriving a descriptor from the framebuffer when needed.
    /// </summary>
    /// <param name="frameBuffer">Framebuffer instance whose Name becomes the logical registry key.</param>
    /// <param name="descriptor">Optional descriptor override to register before binding.</param>
    public void BindFrameBuffer(XRFrameBuffer frameBuffer, FrameBufferResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        string name = frameBuffer.Name ?? throw new InvalidOperationException("FrameBuffer name must be set before binding to the registry.");

        RenderFrameBufferResource record;
        if (descriptor is null && _frameBuffers.TryGetValue(name, out RenderFrameBufferResource? existingRecord))
        {
            record = existingRecord;
        }
        else
        {
            descriptor ??= RenderResourceDescriptorFactory.FromFrameBuffer(frameBuffer);
            descriptor = descriptor with { Name = name };
            record = RegisterFrameBufferDescriptor(descriptor);
        }

        record.Bind(frameBuffer);
    }

    /// <summary>
    /// Binds a live data buffer to the registry, deriving a descriptor from the buffer when needed.
    /// </summary>
    /// <param name="buffer">Buffer instance whose AttributeName becomes the logical registry key.</param>
    /// <param name="descriptor">Optional descriptor override to register before binding.</param>
    public void BindBuffer(XRDataBuffer buffer, BufferResourceDescriptor? descriptor = null, bool ownsInstance = true)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Data buffers are addressed by attribute name rather than optional display name because
        // vertex layout and shader binding code already use AttributeName as the stable key.
        string name = buffer.AttributeName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Data buffer attribute name must be set before binding to the registry.");

        RenderBufferResource record;
        if (descriptor is null && _buffers.TryGetValue(name, out RenderBufferResource? existingRecord))
        {
            record = existingRecord;
        }
        else
        {
            descriptor ??= RenderResourceDescriptorFactory.FromBuffer(buffer);
            descriptor = descriptor with { Name = name };
            record = RegisterBufferDescriptor(descriptor);
        }

        record.Bind(buffer, ownsInstance);
    }

    /// <summary>
    /// Registers or updates a renderbuffer descriptor and returns its registry record.
    /// </summary>
    public RenderRenderBufferResource RegisterRenderBufferDescriptor(RenderBufferResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_renderBuffers.TryGetValue(descriptor.Name, out RenderRenderBufferResource? record))
        {
            UpdateRenderBufferDescriptorIfChanged(record, descriptor);
            return record;
        }

        RenderRenderBufferResource newRecord = new(descriptor);
        record = _renderBuffers.GetOrAdd(descriptor.Name, newRecord);
        if (ReferenceEquals(record, newRecord))
            MarkDescriptorsChanged();
        else
            UpdateRenderBufferDescriptorIfChanged(record, descriptor);

        return record;
    }

    /// <summary>
    /// Binds a live renderbuffer to the registry, deriving a descriptor from the renderbuffer when needed.
    /// </summary>
    /// <param name="renderBuffer">Renderbuffer instance whose Name becomes the logical registry key.</param>
    /// <param name="descriptor">Optional descriptor override to register before binding.</param>
    public void BindRenderBuffer(XRRenderBuffer renderBuffer, RenderBufferResourceDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(renderBuffer);
        string name = renderBuffer.Name ?? throw new InvalidOperationException("RenderBuffer name must be set before binding to the registry.");

        RenderRenderBufferResource record;
        if (descriptor is null && _renderBuffers.TryGetValue(name, out RenderRenderBufferResource? existingRecord))
        {
            record = existingRecord;
        }
        else
        {
            descriptor ??= RenderResourceDescriptorFactory.FromRenderBuffer(renderBuffer);
            descriptor = descriptor with { Name = name };
            record = RegisterRenderBufferDescriptor(descriptor);
        }

        record.Bind(renderBuffer);
    }

    /// <summary>
    /// Resolves a live texture by logical resource name.
    /// </summary>
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

    /// <summary>
    /// Resolves a live framebuffer by logical resource name.
    /// </summary>
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

    /// <summary>
    /// Resolves a live data buffer by logical resource name.
    /// </summary>
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

    /// <summary>
    /// Resolves a live renderbuffer by logical resource name.
    /// </summary>
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

    /// <summary>
    /// Enumerates currently bound texture instances, skipping descriptor-only records.
    /// </summary>
    public IEnumerable<XRTexture> EnumerateTextureInstances()
    {
        foreach (RenderTextureResource record in _textures.Values)
            if (record.Instance is XRTexture tex)
                yield return tex;
    }

    /// <summary>
    /// Enumerates currently bound framebuffer instances, skipping descriptor-only records.
    /// </summary>
    public IEnumerable<XRFrameBuffer> EnumerateFrameBufferInstances()
    {
        foreach (RenderFrameBufferResource record in _frameBuffers.Values)
            if (record.Instance is XRFrameBuffer fbo)
                yield return fbo;
    }

    /// <summary>
    /// Enumerates currently bound data-buffer instances, skipping descriptor-only records.
    /// </summary>
    public IEnumerable<XRDataBuffer> EnumerateBufferInstances()
    {
        foreach (RenderBufferResource record in _buffers.Values)
            if (record.Instance is XRDataBuffer buffer)
                yield return buffer;
    }

    /// <summary>
    /// Enumerates currently bound renderbuffer instances, skipping descriptor-only records.
    /// </summary>
    public IEnumerable<XRRenderBuffer> EnumerateRenderBufferInstances()
    {
        foreach (RenderRenderBufferResource record in _renderBuffers.Values)
            if (record.Instance is XRRenderBuffer rb)
                yield return rb;
    }

    /// <summary>
    /// Removes a texture descriptor and destroys its live texture instance.
    /// </summary>
    /// <remarks>
    /// Any live framebuffer that references the removed texture is destroyed as well so future users
    /// do not render through attachments that point at a destroyed texture.
    /// </remarks>
    public void RemoveTexture(string name)
    {
        if (!_textures.TryRemove(name, out RenderTextureResource? record))
            return;
        
        if (record.Instance is XRTexture texture)
            DestroyFrameBuffersReferencing(texture);

        record.DestroyInstance();
        MarkDescriptorsChanged();
    }

    /// <summary>
    /// Removes a framebuffer descriptor and destroys its live framebuffer instance.
    /// </summary>
    public void RemoveFrameBuffer(string name)
    {
        if (!_frameBuffers.TryRemove(name, out RenderFrameBufferResource? record))
            return;
        
        record.DestroyInstance();
        MarkDescriptorsChanged();
    }

    /// <summary>
    /// Removes a data-buffer descriptor and destroys its live buffer instance.
    /// </summary>
    public void RemoveBuffer(string name)
    {
        if (!_buffers.TryRemove(name, out RenderBufferResource? record))
            return;
        
        record.DestroyInstance();
        MarkDescriptorsChanged();
    }

    /// <summary>
    /// Removes a renderbuffer descriptor and destroys its live renderbuffer instance.
    /// </summary>
    public void RemoveRenderBuffer(string name)
    {
        if (_renderBuffers.TryRemove(name, out RenderRenderBufferResource? record))
        {
            record.DestroyInstance();
            MarkDescriptorsChanged();
        }
    }

    /// <summary>
    /// Destroys all live resource instances, optionally keeping descriptors for later re-materialization.
    /// </summary>
    /// <param name="retainDescriptors">
    /// When <c>true</c>, preserves logical descriptors so the renderer can rebuild physical resources.
    /// When <c>false</c>, clears descriptors and advances DescriptorRevision when anything existed.
    /// </param>
    public void DestroyAllPhysicalResources(bool retainDescriptors = false)
    {
        // Framebuffers own attachment bindings, so destroy them before the textures and renderbuffers
        // they may reference.
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
            bool hadDescriptors =
                !_textures.IsEmpty ||
                !_frameBuffers.IsEmpty ||
                !_buffers.IsEmpty ||
                !_renderBuffers.IsEmpty;

            _textures.Clear();
            _frameBuffers.Clear();
            _buffers.Clear();
            _renderBuffers.Clear();

            if (hadDescriptors)
                MarkDescriptorsChanged();
        }
    }

    /// <summary>
    /// Destroys live framebuffers that still reference a texture being removed.
    /// </summary>
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

    /// <summary>
    /// Advances the descriptor revision so cached signatures and dependent planners are invalidated.
    /// </summary>
    private void MarkDescriptorsChanged()
        => Interlocked.Increment(ref _descriptorRevision);

    /// <summary>
    /// Updates a texture descriptor and invalidates the descriptor signature when it changed materially.
    /// </summary>
    private void UpdateTextureDescriptorIfChanged(RenderTextureResource record, TextureResourceDescriptor descriptor)
    {
        if (ReferenceEquals(record.Descriptor, descriptor))
            return;

        if (!EqualityComparer<TextureResourceDescriptor>.Default.Equals(record.Descriptor, descriptor))
            MarkDescriptorsChanged();

        record.UpdateDescriptor(descriptor);
    }

    /// <summary>
    /// Updates a framebuffer descriptor and invalidates the descriptor signature when it changed materially.
    /// </summary>
    private void UpdateFrameBufferDescriptorIfChanged(RenderFrameBufferResource record, FrameBufferResourceDescriptor descriptor)
    {
        if (ReferenceEquals(record.Descriptor, descriptor))
            return;

        if (!FrameBufferDescriptorsEquivalent(record.Descriptor, descriptor))
            MarkDescriptorsChanged();

        record.UpdateDescriptor(descriptor);
    }

    /// <summary>
    /// Updates a data-buffer descriptor and invalidates the descriptor signature when it changed materially.
    /// </summary>
    private void UpdateBufferDescriptorIfChanged(RenderBufferResource record, BufferResourceDescriptor descriptor)
    {
        if (ReferenceEquals(record.Descriptor, descriptor))
            return;

        if (!EqualityComparer<BufferResourceDescriptor>.Default.Equals(record.Descriptor, descriptor))
            MarkDescriptorsChanged();

        record.UpdateDescriptor(descriptor);
    }

    /// <summary>
    /// Updates a renderbuffer descriptor and invalidates the descriptor signature when it changed materially.
    /// </summary>
    private void UpdateRenderBufferDescriptorIfChanged(RenderRenderBufferResource record, RenderBufferResourceDescriptor descriptor)
    {
        if (ReferenceEquals(record.Descriptor, descriptor))
            return;

        if (!EqualityComparer<RenderBufferResourceDescriptor>.Default.Equals(record.Descriptor, descriptor))
            MarkDescriptorsChanged();

        record.UpdateDescriptor(descriptor);
    }

    /// <summary>
    /// Computes a deterministic hash for descriptor state consumed by Vulkan resource planning.
    /// </summary>
    private int ComputeDescriptorSignature()
    {
        HashCode hash = new();

        // Sort by case-insensitive key so dictionary enumeration order does not affect cache keys.
        foreach (KeyValuePair<string, RenderTextureResource> pair in _textures.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextureResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.FormatLabel, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.ArrayLayers);
            hash.Add(descriptor.StereoCompatible);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.RequiresStorageUsage);
            hash.Add((int)descriptor.Kind);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.InternalFormat.HasValue ? (int)descriptor.InternalFormat.Value : -1);
            hash.Add(descriptor.PixelFormat.HasValue ? (int)descriptor.PixelFormat.Value : -1);
            hash.Add(descriptor.PixelType.HasValue ? (int)descriptor.PixelType.Value : -1);
            hash.Add(descriptor.SizedInternalFormat.HasValue ? (int)descriptor.SizedInternalFormat.Value : -1);
            hash.Add(descriptor.Samples);
            hash.Add(descriptor.MipPolicy.BaseMipLevel);
            hash.Add(descriptor.MipPolicy.MipLevelCount);
            hash.Add(descriptor.MipPolicy.AutoGenerateMipmaps);
            hash.Add(descriptor.MipPolicy.RequireImmutableStorage);
            hash.Add(descriptor.SourceTextureName, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.BaseMipLevel);
            hash.Add(descriptor.MipLevelCount);
            hash.Add(descriptor.BaseLayer);
            hash.Add(descriptor.LayerCount);
            hash.Add((int)descriptor.DepthStencilAspect);
            hash.Add(descriptor.ArrayTarget);
            hash.Add(descriptor.Multisample);
        }

        // Include attachment order. Framebuffer attachments are semantically ordered by their
        // descriptor list, so a reordered list should invalidate framebuffer planning.
        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in _frameBuffers.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            FrameBufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.Attachments.Count);

            foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
            {
                hash.Add(attachment.ResourceName, StringComparer.OrdinalIgnoreCase);
                hash.Add((int)attachment.Attachment);
                hash.Add(attachment.MipLevel);
                hash.Add(attachment.LayerIndex);
            }
        }

        // Buffer descriptors affect Vulkan allocation size, target, usage, and aliasing decisions.
        foreach (KeyValuePair<string, RenderBufferResource> pair in _buffers.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            BufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add(descriptor.SizeInBytes);
            hash.Add((int)descriptor.Target);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.SupportsAliasing);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares framebuffer descriptors with case-insensitive resource names.
    /// </summary>
    /// <remarks>
    /// The generated record equality would compare attachment resource names case-sensitively. The
    /// registry keys are case-insensitive, so framebuffer equivalence follows the same rule.
    /// </remarks>
    private static bool FrameBufferDescriptorsEquivalent(
        FrameBufferResourceDescriptor left,
        FrameBufferResourceDescriptor right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
            left.Lifetime != right.Lifetime ||
            left.SizePolicy != right.SizePolicy ||
            left.Attachments.Count != right.Attachments.Count)
            return false;

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
                return false;
        }

        return true;
    }
}
