namespace XREngine.Animation.Importers;

/// <summary>
/// Unity material animation payload classification retained from the source.
/// </summary>
public enum SerializedMaterialAnimationValueKind
{
    Float,
    Int,
    Vector,
    Color,
    Texture,
    ObjectReference,
}
