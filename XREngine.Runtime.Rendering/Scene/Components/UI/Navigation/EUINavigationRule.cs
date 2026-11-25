namespace XREngine.Rendering.UI;

/// <summary>
/// Controls how a single navigation direction from a UI element resolves its next focus target.
/// </summary>
public enum EUINavigationRule
{
    /// <summary>
    /// Follows an assigned target if eligible; otherwise uses spatial fallback or axis wrap when enabled.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Follows only the assigned target link and stops if no eligible target is linked.
    /// </summary>
    Explicit = 1,

    /// <summary>
    /// Disallows navigation in this direction even if another element sits along that axis.
    /// </summary>
    Blocked = 2,

    /// <summary>
    /// Follows an assigned target if eligible; otherwise wraps to the opposite end along the same axis.
    /// </summary>
    Wrap = 3,
}
