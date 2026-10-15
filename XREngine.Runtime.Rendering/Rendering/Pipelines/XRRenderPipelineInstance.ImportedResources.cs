using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    private readonly object _importedResourceStagingLock = new();
    private readonly Dictionary<string, (XRTexture Instance, TextureResourceDescriptor Descriptor)> _stagedImportedTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (XRDataBuffer Instance, BufferResourceDescriptor Descriptor)> _stagedImportedBuffers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stagedImportedTextureRemovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stagedImportedBufferRemovals = new(StringComparer.OrdinalIgnoreCase);
    private int _importedResourceStagingRevision;

    /// <summary>
    /// Stages a caller-owned texture for publication at the next frame collection boundary.
    /// </summary>
    /// <returns><see langword="true"/> when this exact binding is already published.</returns>
    internal bool BindImportedTexture(XRTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        string name = texture.Name ?? throw new InvalidOperationException(
            "Imported texture name must be set before binding.");
        RenderResourceRegistry registry = GetImportedResourceRegistry(
            name,
            ExternalRenderResourceKind.Texture);
        TextureResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromTexture(texture) with
        {
            Name = name,
            Lifetime = RenderResourceLifetime.External,
        };

        XRTexture? replacedTexture = null;
        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedTextureRemovals.Remove(name);
            if (!_stagedImportedTextures.TryGetValue(name, out var staged) ||
                !ReferenceEquals(staged.Instance, texture) ||
                !Equals(staged.Descriptor, descriptor))
            {
                bool replacingTexture = !ReferenceEquals(staged.Instance, texture);
                if (replacingTexture && !texture.TryRetainActiveBindingUse())
                    throw new ObjectDisposedException(nameof(XRTexture));

                _stagedImportedTextures[name] = (texture, descriptor);
                if (replacingTexture)
                    replacedTexture = staged.Instance;
                changed = true;
            }

            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
        }

        replacedTexture?.ReleaseBindingUse();
        return IsImportedTexturePublished(registry, name, texture, descriptor);
    }

    /// <summary>
    /// Stages a caller-owned buffer for publication at the next frame collection boundary.
    /// </summary>
    /// <returns><see langword="true"/> when this exact binding is already published.</returns>
    internal bool BindImportedBuffer(XRDataBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        string name = buffer.AttributeName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Imported buffer attribute name must be set before binding.");

        RenderResourceRegistry registry = GetImportedResourceRegistry(
            name,
            ExternalRenderResourceKind.Buffer);
        BufferResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromBuffer(buffer) with
        {
            Name = name,
            Lifetime = RenderResourceLifetime.External,
        };

        XRDataBuffer? replacedBuffer = null;
        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedBufferRemovals.Remove(name);
            if (!_stagedImportedBuffers.TryGetValue(name, out var staged) ||
                !ReferenceEquals(staged.Instance, buffer) ||
                !Equals(staged.Descriptor, descriptor))
            {
                bool replacingBuffer = !ReferenceEquals(staged.Instance, buffer);
                if (replacingBuffer && !buffer.TryRetainActiveBindingUse())
                    throw new ObjectDisposedException(nameof(XRDataBuffer));

                _stagedImportedBuffers[name] = (buffer, descriptor);
                if (replacingBuffer)
                    replacedBuffer = staged.Instance;
                changed = true;
            }

            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
        }

        replacedBuffer?.ReleaseBindingUse();
        return IsImportedBufferPublished(registry, name, buffer, descriptor);
    }

    internal bool UnbindImportedTexture(string name)
    {
        bool published = TryGetImportedResourceRegistry(
            name,
            ExternalRenderResourceKind.Texture,
            out RenderResourceRegistry? registry) &&
            registry.TryGetTexture(name, out _);
        XRTexture? unboundTexture = null;
        bool result;
        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedTextures.Remove(name, out var staged);
            if (changed)
                unboundTexture = staged.Instance;
            changed |= _stagedImportedTextureRemovals.Add(name);
            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
            result = published || changed;
        }

        unboundTexture?.ReleaseBindingUse();
        return result;
    }

    internal bool UnbindImportedBuffer(string name)
    {
        bool published = TryGetImportedResourceRegistry(
            name,
            ExternalRenderResourceKind.Buffer,
            out RenderResourceRegistry? registry) &&
            registry.TryGetBuffer(name, out _);
        XRDataBuffer? unboundBuffer = null;
        bool result;
        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedBuffers.Remove(name, out var staged);
            if (changed)
                unboundBuffer = staged.Instance;
            changed |= _stagedImportedBufferRemovals.Add(name);
            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
            result = published || changed;
        }

        unboundBuffer?.ReleaseBindingUse();
        return result;
    }

    /// <summary>
    /// Atomically publishes the latest caller-owned bindings into one resource generation.
    /// The returned revision identifies the exact staged set that was published.
    /// </summary>
    internal int PublishStagedImportedResources(RenderResourceGeneration? generation)
    {
        if (generation is null)
            return ImportedResourceStagingRevision;

        bool bindingsChanged = false;
        RenderResourceChangeKind strongestChange = RenderResourceChangeKind.CompatibleContentPublication;
        int publishedRevision;
        lock (_importedResourceStagingLock)
        {
            RenderResourceRegistry registry = generation.Registry;
            foreach (var pair in _stagedImportedTextures)
            {
                if (!DeclaresImportedResource(generation, pair.Key, ExternalRenderResourceKind.Texture))
                    continue;

                registry.TryGetTexture(pair.Key, out XRTexture? existing);
                if (IsImportedTexturePublished(registry, pair.Key, pair.Value.Instance, pair.Value.Descriptor))
                    continue;

                strongestChange = MaxChangeKind(
                    strongestChange,
                    ClassifyTextureBindingChange(registry, pair.Key, pair.Value.Descriptor, existing));
                registry.BindTransferredTexture(pair.Value.Instance, pair.Value.Descriptor);
                bindingsChanged = true;
            }

            foreach (var pair in _stagedImportedBuffers)
            {
                if (!DeclaresImportedResource(generation, pair.Key, ExternalRenderResourceKind.Buffer))
                    continue;

                registry.TryGetBuffer(pair.Key, out XRDataBuffer? existing);
                if (IsImportedBufferPublished(registry, pair.Key, pair.Value.Instance, pair.Value.Descriptor))
                    continue;

                strongestChange = MaxChangeKind(
                    strongestChange,
                    ClassifyBufferBindingChange(registry, pair.Key, pair.Value.Descriptor, existing));
                registry.BindTransferredBuffer(pair.Value.Instance, pair.Value.Descriptor);
                bindingsChanged = true;
            }

            foreach (string name in _stagedImportedTextureRemovals)
            {
                if (generation.Registry.TryGetTexture(name, out _))
                {
                    generation.Registry.RemoveTexture(name);
                    bindingsChanged = true;
                    strongestChange = RenderResourceChangeKind.StructuralLayout;
                }
            }

            foreach (string name in _stagedImportedBufferRemovals)
            {
                if (generation.Registry.TryGetBuffer(name, out _))
                {
                    generation.Registry.RemoveBuffer(name);
                    bindingsChanged = true;
                    strongestChange = RenderResourceChangeKind.StructuralLayout;
                }
            }

            publishedRevision = _importedResourceStagingRevision;
        }

        if (bindingsChanged)
            NotifyRenderResourcesChanged(strongestChange, nameof(PublishStagedImportedResources));
        return publishedRevision;
    }

    internal int ImportedResourceStagingRevision
    {
        get
        {
            lock (_importedResourceStagingLock)
                return _importedResourceStagingRevision;
        }
    }

    private static bool DeclaresImportedResource(
        RenderResourceGeneration generation,
        string name,
        ExternalRenderResourceKind expectedKind)
        => generation.Layout.ResourcesByName.TryGetValue(name, out RenderPipelineResourceSpec? spec) &&
           spec is ExternalResourceSpec external &&
           external.ExternalKind == expectedKind;

    private static bool IsImportedTexturePublished(
        RenderResourceRegistry registry,
        string name,
        XRTexture texture,
        TextureResourceDescriptor descriptor)
        => registry.TextureRecords.TryGetValue(name, out RenderTextureResource? record) &&
           ReferenceEquals(record.Instance, texture) &&
           !record.OwnsInstance &&
           Equals(record.Descriptor, descriptor);

    private static bool IsImportedBufferPublished(
        RenderResourceRegistry registry,
        string name,
        XRDataBuffer buffer,
        BufferResourceDescriptor descriptor)
        => registry.BufferRecords.TryGetValue(name, out RenderBufferResource? record) &&
           ReferenceEquals(record.Instance, buffer) &&
           !record.OwnsInstance &&
           Equals(record.Descriptor, descriptor);

    private static RenderResourceChangeKind MaxChangeKind(
        RenderResourceChangeKind first,
        RenderResourceChangeKind second)
        => first >= second ? first : second;

    private void AdvanceImportedResourceStagingRevisionNoLock()
    {
        int revision = unchecked(_importedResourceStagingRevision + 1);
        _importedResourceStagingRevision = revision == 0 ? 1 : revision;
    }

    private void ClearStagedImportedBindings()
    {
        Exception? firstFailure = null;
        lock (_importedResourceStagingLock)
        {
            foreach (var staged in _stagedImportedTextures.Values)
            {
                try
                {
                    staged.Instance.ReleaseBindingUse();
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }

            foreach (var staged in _stagedImportedBuffers.Values)
            {
                try
                {
                    staged.Instance.ReleaseBindingUse();
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }

            _stagedImportedBuffers.Clear();
            _stagedImportedTextures.Clear();
            _stagedImportedBufferRemovals.Clear();
            _stagedImportedTextureRemovals.Clear();
            AdvanceImportedResourceStagingRevisionNoLock();
        }

        if (firstFailure is not null)
            throw new InvalidOperationException("A staged imported binding could not be released.", firstFailure);
    }
}
