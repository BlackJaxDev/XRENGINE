namespace XREngine.Animation.Importers;

/// <summary>
/// Durable source metadata for one Unity material animation binding.
/// </summary>
public sealed record UnityMaterialAnimationBinding(
    string NodePath,
    string OriginalAttribute,
    string SourceProperty,
    string SemanticProperty,
    int MaterialSlot,
    int Component,
    UnityMaterialAnimationValueKind ValueKind,
    int? ClassId);
