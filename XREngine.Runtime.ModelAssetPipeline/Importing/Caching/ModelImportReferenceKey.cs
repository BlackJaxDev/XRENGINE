namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable material, texture, animation, or extension key exposed for project remapping.
/// </summary>
public sealed class ModelImportReferenceKey
{
    public ModelImportReferenceKey(ModelImportReferenceKind kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Kind = kind;
        Key = key;
    }

    public ModelImportReferenceKind Kind { get; }
    public string Key { get; }
}
