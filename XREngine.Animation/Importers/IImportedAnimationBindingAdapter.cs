namespace XREngine.Animation.Importers;

/// <summary>
/// Explicit bridge for a Unity component/property that has no native XRE target.
/// Implementations live on the animated node and are validated before playback.
/// </summary>
public interface IImportedAnimationBindingAdapter
{
    bool CanBind(ImportedAnimationBindingDescriptor binding, out string diagnostic);
    /// <summary>
    /// Reads the current scalar value before playback. Quaternion adapters must
    /// expose all four components so direct clip weighting can blend from the
    /// real target pose rather than assuming an identity baseline.
    /// </summary>
    bool TryGetFloat(ImportedAnimationBindingDescriptor binding, out float value, out string diagnostic);
    bool TrySetFloat(ImportedAnimationBindingDescriptor binding, float value, out string diagnostic);
    bool TrySetObjectReference(ImportedAnimationBindingDescriptor binding, SourceAssetReference value, out string diagnostic);
}
