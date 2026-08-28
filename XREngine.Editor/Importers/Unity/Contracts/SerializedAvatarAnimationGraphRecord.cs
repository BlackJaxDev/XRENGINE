namespace XREngine.Scene.Prefabs;

/// <summary>
/// Import-side evidence needed by a future compiler that can merge source avatar animator layers
/// and expression parameters into one native animation state machine component.
/// </summary>
[Serializable]
public sealed class SerializedAvatarAnimationGraphRecord
{
    public SourceAssetIdentity DescriptorIdentity { get; set; } = new();
    public string SceneNodePath { get; set; } = string.Empty;
    public SourceAssetIdentity? AnimationPreset { get; set; }
    public List<SerializedAvatarAnimationLayer> Layers { get; set; } = [];
    public bool UsesCustomExpressions { get; set; }
    public SourceAssetIdentity? ExpressionMenu { get; set; }
    public SourceAssetIdentity? ExpressionParameters { get; set; }
}
