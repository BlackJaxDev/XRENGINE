namespace XREngine.Rendering;

/// <summary>
/// Persistent and per-frame visibility preparation state.
/// </summary>
[Flags]
public enum EAdvancedVisibilityPreparationFlags : uint
{
    None = 0u,
    NewRecord = 1u << 0,
    ResizedView = 1u << 1,
    InvalidHistory = 1u << 2,
    Uncertain = 1u << 3,
    ConservativeVisible = 1u << 4,
    EarlyVisible = 1u << 5,
    Deferred = 1u << 6,
    LateVisible = 1u << 7,
    Occluded = 1u << 8,
}
