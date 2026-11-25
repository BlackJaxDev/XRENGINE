using System.Numerics;
using XREngine.Rendering.UI;

/// <summary>Adapts the editor scroll transform to the shared scroll layout.</summary>
public class UIScrollingTransform : UIScrollableTransform
{
    /// <summary>Positive X scrolls right and positive Y scrolls down.</summary>
    public Vector2 ScrollPosition
    {
        get => ScrollOffset;
        set => ScrollOffset = value;
    }

    /// <summary>Gets the measured content size of the scroll region.</summary>
    public Vector2 ScrollSize => ContentExtent;
}
