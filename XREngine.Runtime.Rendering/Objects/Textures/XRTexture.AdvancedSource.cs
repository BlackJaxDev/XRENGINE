namespace XREngine.Rendering;

public abstract partial class XRTexture
{
    private long _advancedSourceVersion = 1L;

    /// <summary>
    /// Gets a version that advances whenever a texture input of the advanced GPU
    /// scene source encoder can change: a property change, a sampler or format
    /// metadata write (also one that publishes no notification), or a change of
    /// the progressive-upload state. A material that binds this texture uses it to
    /// skip the full source encode while the version stays the same. Mip content
    /// and imported streaming state use their own counters
    /// (<c>CanonicalSourceContentGeneration</c>, <c>ImportedSourceMetadataEpoch</c>,
    /// and the mip content epochs), which a witness reads directly.
    /// </summary>
    internal ulong AdvancedSourceVersion
        => unchecked((ulong)Volatile.Read(ref _advancedSourceVersion));

    /// <summary>Raised after <see cref="AdvancedSourceVersion"/> advances.</summary>
    internal event Action<XRTexture>? AdvancedSourceChanged;

    /// <summary>
    /// Called by a renderer backend after it replaced or retired the image view that
    /// descriptors bind for this texture, for example when a streamed upload publishes
    /// a new image. Consumers that cache encoded descriptor sources by
    /// <see cref="AdvancedSourceVersion"/> must not keep the old view.
    /// </summary>
    internal void NotifyBackendDescriptorSourceChanged()
        => AdvanceAdvancedSourceVersion();

    /// <summary>Advances <see cref="AdvancedSourceVersion"/> and notifies listeners.</summary>
    protected void AdvanceAdvancedSourceVersion()
    {
        if (Interlocked.Increment(ref _advancedSourceVersion) == 0L)
            Interlocked.Increment(ref _advancedSourceVersion);
        AdvancedSourceChanged?.Invoke(this);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        AdvanceAdvancedSourceVersion();
    }
}
