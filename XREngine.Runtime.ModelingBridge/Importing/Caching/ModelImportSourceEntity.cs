namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable producer identity for an entity found in the source model.
/// </summary>
public sealed class ModelImportSourceEntity
{
    public ModelImportSourceEntity(
        string key,
        ModelImportEntityKind kind,
        string? diagnosticName,
        bool isStable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Key = key;
        Kind = kind;
        DiagnosticName = string.IsNullOrWhiteSpace(diagnosticName) ? null : diagnosticName;
        IsStable = isStable;
    }

    public string Key { get; }
    public ModelImportEntityKind Kind { get; }
    public string? DiagnosticName { get; }
    public bool IsStable { get; }
}
