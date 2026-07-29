using XREngine.Scene.Prefabs;

namespace XREngine.Components;

/// <summary>
/// One playable animation layer retained from a Unity avatar descriptor.
/// </summary>
[Serializable]
public sealed class UnityAvatarAnimationLayer
{
    public int LayerType { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public UnityAssetIdentity? Controller { get; set; }
    public UnityAssetIdentity? Mask { get; set; }
}
