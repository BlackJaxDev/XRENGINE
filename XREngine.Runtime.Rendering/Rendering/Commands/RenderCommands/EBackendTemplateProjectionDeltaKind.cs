namespace XREngine.Rendering.Commands;

/// <summary>
/// Structural-only change requested from a backend template projection.
/// </summary>
public enum EBackendTemplateProjectionDeltaKind : byte
{
    None,
    Add,
    Update,
    Tombstone,
    DenseRemap,
}
