using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Conservative visible ellipses of the first two views of an Advanced stage request, each as
/// (center u, center v, radius u, radius v) in eye-image UV. A zero ellipse means that the view
/// keeps every tile. <see cref="AdvancedHiddenAreaTiles"/> fills it for the OpenXR eye views.
/// </summary>
public readonly record struct AdvancedHiddenAreaEllipses(Vector4 View0, Vector4 View1)
{
    /// <summary>Returns the ellipse of a view, or zero for a view after the first two.</summary>
    public Vector4 ForView(uint viewIndex)
        => viewIndex switch
        {
            0u => View0,
            1u => View1,
            _ => Vector4.Zero,
        };
}
