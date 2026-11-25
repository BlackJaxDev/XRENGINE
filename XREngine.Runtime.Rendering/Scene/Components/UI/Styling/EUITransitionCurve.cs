namespace XREngine.Rendering.UI;

/// <summary>
/// Interpolation curve used by <see cref="UITransitionSpec"/> to animate UI style properties.
/// </summary>
public enum EUITransitionCurve
{
    /// <summary>
    /// Constant-rate linear interpolation.
    /// </summary>
    Linear = 0,

    /// <summary>
    /// Fast start with smooth cubic deceleration.
    /// </summary>
    EaseOutCubic = 1,

    /// <summary>
    /// Smooth symmetric cubic acceleration and deceleration.
    /// </summary>
    EaseInOutCubic = 2,

    /// <summary>
    /// Responsive deceleration curve matching Apple HIG cubic-bezier(0.22, 1.0, 0.36, 1.0).
    /// </summary>
    AppleEaseOut = 3,

    /// <summary>
    /// Physics-based damped spring driven by stiffness and damping coefficients.
    /// </summary>
    Spring = 4,
}
