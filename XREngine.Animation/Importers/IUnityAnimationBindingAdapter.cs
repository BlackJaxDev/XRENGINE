namespace XREngine.Animation.Importers;

/// <summary>
/// Explicit bridge for a Unity component/property that has no native XRE target.
/// Implementations live on the animated node and are validated before playback.
/// </summary>
public interface IUnityAnimationBindingAdapter
{
    bool CanBind(UnityAnimationBindingDescriptor binding, out string diagnostic);
    /// <summary>
    /// Reads the current scalar value before playback. Quaternion adapters must
    /// expose all four components so direct clip weighting can blend from the
    /// real target pose rather than assuming an identity baseline.
    /// </summary>
    bool TryGetFloat(UnityAnimationBindingDescriptor binding, out float value, out string diagnostic);
    bool TrySetFloat(UnityAnimationBindingDescriptor binding, float value, out string diagnostic);
    bool TrySetObjectReference(UnityAnimationBindingDescriptor binding, UnityAssetReference value, out string diagnostic);
}
