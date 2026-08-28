namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Connects a serialized Unity property name to its original Poiyomi semantic identity.
/// </summary>
public sealed record SourceToonPropertyBinding
{
    public required string SourceName { get; init; }
    public required string SemanticName { get; init; }
    public bool IsAnimated { get; init; }
    public bool IsRenamed { get; init; }
}
