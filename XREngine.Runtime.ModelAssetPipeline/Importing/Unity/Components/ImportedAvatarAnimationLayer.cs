using XREngine.Scene.Prefabs;

namespace XREngine.Components;

/// <summary>
/// One playable animation layer retained from a Unity avatar descriptor.
/// </summary>
[Serializable]
public sealed class ImportedAvatarAnimationLayer
{
    public int LayerType { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public SourceAssetIdentity? Controller { get; set; }
    public SourceAssetIdentity? Mask { get; set; }
}
