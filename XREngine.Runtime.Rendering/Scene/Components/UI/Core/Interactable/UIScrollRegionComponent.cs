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
    public override string? AccessibilityValue
    {
        get
        {
            Vector2 offset = ScrollTransform.ScrollOffset;
            return FormattableString.Invariant($"{offset.X:0.##}, {offset.Y:0.##} pixels");
        }
    }

    public override bool AccessibilityAdjust(int direction)
        => direction != 0 && ScrollBy(new Vector2(0, Math.Sign(direction) * WheelStepPixels));

    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction == Vector2.Zero)
            return false;
        return MathF.Abs(direction.Y) >= MathF.Abs(direction.X)
            ? ScrollBy(new Vector2(0, -Math.Sign(direction.Y) * WheelStepPixels))
            : ScrollBy(new Vector2(Math.Sign(direction.X) * WheelStepPixels, 0));
    }

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
        return ScrollBy(new Vector2(0, -diff * WheelStepPixels)) || ConsumeWheelAtBoundary;
    }

    private bool ScrollBy(Vector2 delta)
    {
        if (!CanInteract || IsReadOnly || !ScrollTransform.Scrollable ||
            delta.X != 0 && !ScrollTransform.ScrollableX ||
            delta.Y != 0 && !ScrollTransform.ScrollableY)
            return false;
        UIScrollableTransform scroll = ScrollTransform;
        if (scroll.ViewportExtent.X <= 0 || scroll.ViewportExtent.Y <= 0)
            return false;
        Vector2 previous = scroll.ScrollOffset;
        scroll.ScrollOffset = previous + delta;
        return scroll.ScrollOffset != previous;
    }

    public void ScrollIntoView(Vector2 contentStart, Vector2 contentEnd)
        => ScrollTransform.ScrollIntoView(contentStart, contentEnd);
}
