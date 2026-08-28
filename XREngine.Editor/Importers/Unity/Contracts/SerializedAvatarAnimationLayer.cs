namespace XREngine.Scene.Prefabs;

/// <summary>
/// One source avatar animation layer retained as import evidence until a native graph compiler
/// can convert it.
/// </summary>
[Serializable]
public sealed class SerializedAvatarAnimationLayer
{
    public string SourceCollection { get; set; } = string.Empty;
    public int SourceIndex { get; set; }
    public int LayerType { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public SourceAssetIdentity? Controller { get; set; }
    public SourceAssetIdentity? Mask { get; set; }
}
