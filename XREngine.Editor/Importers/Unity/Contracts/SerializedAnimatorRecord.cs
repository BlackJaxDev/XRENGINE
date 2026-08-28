namespace XREngine.Scene.Prefabs;

/// <summary>
/// Import-side record for source animator settings that have not been compiled into a native
/// animation state machine.
/// </summary>
[Serializable]
public sealed class SerializedAnimatorRecord
{
    public SourceAssetIdentity Identity { get; set; } = new();
    public string SceneNodePath { get; set; } = string.Empty;
    public SourceAssetIdentity? Controller { get; set; }
    public bool Enabled { get; set; }
    public bool ApplyRootMotion { get; set; }
    public int CullingMode { get; set; }
    public int UpdateMode { get; set; }
    public bool HasTransformHierarchy { get; set; } = true;
}
