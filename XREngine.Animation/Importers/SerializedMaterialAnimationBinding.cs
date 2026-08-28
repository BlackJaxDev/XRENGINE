namespace XREngine.Animation.Importers;

/// <summary>
/// Durable source metadata for one Unity material animation binding.
/// </summary>
public sealed record SerializedMaterialAnimationBinding(
    string NodePath,
    string OriginalAttribute,
    string SourceProperty,
    string SemanticProperty,
    int MaterialSlot,
    int Component,
    SerializedMaterialAnimationValueKind ValueKind,
    int? ClassId);
