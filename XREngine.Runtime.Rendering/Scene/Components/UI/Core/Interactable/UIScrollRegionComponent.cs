using System.Numerics;
using XREngine.Core.Attributes;

namespace XREngine.Rendering.UI;

/// <summary>Routes wheel and programmatic scrolling to a retained scroll transform.</summary>
[RequiresTransform(typeof(UIScrollableTransform))]
public class UIScrollRegionComponent : UIInteractableComponent, IUIScrollReceiver
{
    private float _wheelStepPixels = 48;
    private bool _consumeWheelAtBoundary;

    public UIScrollableTransform ScrollTransform => TransformAs<UIScrollableTransform>(true)!;
    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.ScrollRegion;

    public float WheelStepPixels
    {
        get => _wheelStepPixels;
        set
        {
            if (!float.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _wheelStepPixels, value);
        }
    }

    /// <summary>Consumes wheel input at an edge instead of passing it to an ancestor.</summary>
    public bool ConsumeWheelAtBoundary
    {
        get => _consumeWheelAtBoundary;
        set => SetField(ref _consumeWheelAtBoundary, value);
    }

    public bool HandleMouseScroll(float diff)
    {
        if (!CanInteract || !float.IsFinite(diff) || diff == 0)
            return false;
        Vector2 previous = ScrollTransform.ScrollOffset;
        ScrollTransform.ScrollOffset = new Vector2(previous.X, previous.Y - diff * WheelStepPixels);
        return ScrollTransform.ScrollOffset != previous || ConsumeWheelAtBoundary;
    }

    public void ScrollIntoView(Vector2 contentStart, Vector2 contentEnd)
        => ScrollTransform.ScrollIntoView(contentStart, contentEnd);
}
