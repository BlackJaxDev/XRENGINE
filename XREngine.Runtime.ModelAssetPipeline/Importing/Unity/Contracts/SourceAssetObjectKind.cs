namespace XREngine.Scene.Prefabs;

/// <summary>
/// Identifies the kind of Unity object represented by a local file identifier.
/// </summary>
public enum SourceAssetObjectKind
{
    Unknown,
    Asset,
    GameObject,
    Transform,
    Component,
    Renderer,
    Mesh,
    Material,
    Texture,
    Animation,
    MonoBehaviour,
}
