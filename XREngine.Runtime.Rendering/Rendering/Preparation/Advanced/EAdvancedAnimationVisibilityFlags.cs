namespace XREngine.Rendering;

/// <summary>
/// Delayed GPU visibility facts consumed by render-pose scheduling.
/// </summary>
[Flags]
public enum EAdvancedAnimationVisibilityFlags : uint
{
    None = 0u,
    Visible = 1u << 0,
    ShadowRelevant = 1u << 1,
    HistoryValid = 1u << 2,
    NewlyVisible = 1u << 3,
    Uncertain = 1u << 4,
}
