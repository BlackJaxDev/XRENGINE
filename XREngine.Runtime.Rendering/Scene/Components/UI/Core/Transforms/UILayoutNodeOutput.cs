using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Numeric result applied after owner validation.</summary>
public struct UILayoutNodeOutput
{
    public Vector2 RawSize;
    public Vector2 DesiredSize;
    public Vector2 MeasureConstraint;
    public Vector2 ActualSize;
    public Vector2 LocalTranslation;
    public Vector2 PlacementTranslation;
    /// <summary>Scroll values captured and accepted with this layout result.</summary>
    public Vector2 AcceptedScrollOffset;
    public Vector2 AcceptedMaxScrollOffset;
    public BoundingRectangleF AllocatedBounds;
    public bool Visible;
    public UITextLayoutResult? TextResult;
}
