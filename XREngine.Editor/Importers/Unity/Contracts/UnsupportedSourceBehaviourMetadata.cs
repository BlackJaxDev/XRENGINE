namespace XREngine.Scene.Prefabs;

/// <summary>
/// Serialized Unity MonoBehaviour data retained for inspection when no adapter exists.
/// </summary>
[Serializable]
public sealed class UnsupportedSourceBehaviourMetadata
{
    public SourceAssetIdentity Identity { get; set; } = new();
    public string SceneNodePath { get; set; } = string.Empty;
    public string ScriptGuid { get; set; } = string.Empty;
    public long ScriptFileId { get; set; }
    public bool Enabled { get; set; } = true;
    public string SerializedYaml { get; set; } = string.Empty;
}
