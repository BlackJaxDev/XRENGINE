using System.ComponentModel;
using System.Text.Json.Serialization;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files;

public abstract partial class XRAsset
{
    private AssetPersistenceDisposition _persistenceDisposition;

    /// <summary>
    /// Gets whether this object is an authoring asset or a runtime-only projection.
    /// </summary>
    [JsonIgnore]
    [YamlIgnore]
    [Browsable(false)]
    [MemoryPackIgnore]
    public AssetPersistenceDisposition PersistenceDisposition
        => _persistenceDisposition;

    /// <summary>
    /// Marks this detached object as a runtime-only projection that cannot become dirty.
    /// </summary>
    public void MarkAsTransientProjection()
    {
        if (!ReferenceEquals(SourceAsset, this) || !string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException(
                "Only detached assets can be marked as runtime-only projections.");
        }

        SetField(
            ref _persistenceDisposition,
            AssetPersistenceDisposition.TransientProjection,
            nameof(PersistenceDisposition));
        ClearDirty();
    }

    /// <inheritdoc/>
    protected override bool OnPropertyChanging<T>(string? propName, T field, T value)
    {
        if (PersistenceDisposition != AssetPersistenceDisposition.TransientProjection)
            return base.OnPropertyChanging(propName, field, value);

        if (propName == nameof(IsDirty) && value is true)
        {
            return false;
        }

        if (propName is nameof(FilePath) or nameof(SourceAsset))
            throw new InvalidOperationException("Runtime-only projections cannot be attached to asset persistence.");

        return base.OnPropertyChanging(propName, field, value);
    }

    /// <summary>
    /// Throws when a runtime-only projection reaches a serialization path.
    /// </summary>
    protected void EnsureCanPersist()
    {
        if (PersistenceDisposition == AssetPersistenceDisposition.TransientProjection)
            throw new InvalidOperationException("Runtime-only projections cannot be saved.");
    }
}
