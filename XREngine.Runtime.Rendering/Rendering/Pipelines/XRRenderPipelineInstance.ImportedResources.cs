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

        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedTextureRemovals.Remove(name);
            if (!_stagedImportedTextures.TryGetValue(name, out var staged) ||
                !ReferenceEquals(staged.Instance, texture) ||
                !Equals(staged.Descriptor, descriptor))
            {
                _stagedImportedTextures[name] = (texture, descriptor);
                changed = true;
            }

            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
        }

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

        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedBufferRemovals.Remove(name);
            if (!_stagedImportedBuffers.TryGetValue(name, out var staged) ||
                !ReferenceEquals(staged.Instance, buffer) ||
                !Equals(staged.Descriptor, descriptor))
            {
                _stagedImportedBuffers[name] = (buffer, descriptor);
                changed = true;
            }

            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
        }

        return IsImportedBufferPublished(registry, name, buffer, descriptor);
    }

    internal bool UnbindImportedTexture(string name)
    {
        if (!TryGetImportedResourceRegistry(
                name,
                ExternalRenderResourceKind.Texture,
                out RenderResourceRegistry? registry))
        {
            return false;
        }

        bool published = registry.TryGetTexture(name, out _);
        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedTextures.Remove(name);
            changed |= _stagedImportedTextureRemovals.Add(name);
            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
            return published || changed;
        }
    }

    internal bool UnbindImportedBuffer(string name)
    {
        if (!TryGetImportedResourceRegistry(
                name,
                ExternalRenderResourceKind.Buffer,
                out RenderResourceRegistry? registry))
        {
            return false;
        }

        bool published = registry.TryGetBuffer(name, out _);
        lock (_importedResourceStagingLock)
        {
            bool changed = _stagedImportedBuffers.Remove(name);
            changed |= _stagedImportedBufferRemovals.Add(name);
            if (changed)
                AdvanceImportedResourceStagingRevisionNoLock();
            return published || changed;
        }
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
                registry.BindTexture(pair.Value.Instance, pair.Value.Descriptor, ownsInstance: false);
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
                registry.BindBuffer(pair.Value.Instance, pair.Value.Descriptor, ownsInstance: false);
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
}
