using System.Numerics;

namespace XREngine.Rendering.UI;

public partial class UITextInputComponent
{
    private float? _preferredCaretX;
    private int? _verticalVisualLine;
    private bool _movingCaretVertically;

    private void Up(bool pressed)
    {
        if (!_terminal && pressed && !SingleLineMode)
            MoveCaretVertically(-1, AnyShift);
    }

    private void Down(bool pressed)
    {
        if (!_terminal && pressed && !SingleLineMode)
            MoveCaretVertically(1, AnyShift);
    }

    /// <summary>Moves to the closest grapheme boundary on the adjacent placed line.</summary>
    public bool MoveCaretVertically(int direction, bool extendSelection = false)
    {
        if (_terminal || SceneNode is null || SceneNode.IsTransformNull ||
            direction == 0 || IsComposing || SingleLineMode ||
            !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout))
            return false;
        return TryMoveCaretVertically(layout, direction, extendSelection);
    }

    /// <summary>Uses only the current accepted placement result for this text field.</summary>
    public bool TryMoveCaretVertically(UITextLayoutResult layout, int direction, bool extendSelection = false)
    {
        if (_terminal || SceneNode is null || SceneNode.IsTransformNull || direction == 0 ||
            IsComposing || SingleLineMode ||
            !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult currentLayout) ||
            !ReferenceEquals(layout, currentLayout) ||
            !layout.HasCompleteGraphemeCaretStops ||
            !TryFindCaretStop(layout.Carets.Span, out UITextCaretStop current))
            return false;

        int targetLine = current.Line + Math.Sign(direction);
        float desiredX = _preferredCaretX ?? current.Position.X;
        int targetOffset = FindNearestCaretOffset(layout.Carets.Span, targetLine,
            desiredX, GraphemeBoundaries(), Text.Length);
        if (targetOffset < 0)
            return false;

        _movingCaretVertically = true;
        try { CursorPosition = targetOffset; }
        finally { _movingCaretVertically = false; }
        _preferredCaretX = desiredX;
        _verticalVisualLine = targetLine;
        if (!extendSelection)
            SelectionAnchor = CursorPosition;
        EnsureCaretVisible();
        return true;
    }

    internal static int FindNearestCaretOffset(ReadOnlySpan<UITextCaretStop> stops, int targetLine,
        float desiredX, int[] graphemeBoundaries, int textLength)
    {
        float bestDistance = float.PositiveInfinity;
        int targetOffset = -1;
        for (int index = 0; index < stops.Length; index++)
        {
            UITextCaretStop stop = stops[index];
            if (stop.Line != targetLine || stop.Utf16Offset != textLength &&
                Array.BinarySearch(graphemeBoundaries, stop.Utf16Offset) < 0)
                continue;
            float distance = MathF.Abs(stop.Position.X - desiredX);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                targetOffset = stop.Utf16Offset;
            }
        }
        return targetOffset;
    }

    /// <summary>Moves the nearest accepted scroll viewport to include the current caret.</summary>
    public bool EnsureCaretVisible()
    {
        if (SceneNode is null || SceneNode.IsTransformNull ||
            !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout) ||
            !TryGetCaretLocalBounds(layout, out Vector4 caret))
            return false;
        UIScrollableTransform? scroll = null;
        for (UITransform? parent = BoundableTransform.Parent as UITransform; parent is not null;
             parent = parent.Parent as UITransform)
            if (parent is UIScrollableTransform viewport)
            {
                scroll = viewport;
                break;
            }
        if (scroll is null || scroll.ViewportExtent.X <= 0 || scroll.ViewportExtent.Y <= 0)
            return false;

        Vector2 bottomLeft = scroll.CanvasToLocal(BoundableTransform.LocalToCanvas(new Vector2(caret.X, caret.Y)));
        Vector2 topRight = scroll.CanvasToLocal(BoundableTransform.LocalToCanvas(
            new Vector2(caret.X + caret.Z, caret.Y + caret.W)));
        Vector2 offset = scroll.ScrollOffset;
        Vector2 start = new(MathF.Min(bottomLeft.X, topRight.X) + offset.X,
            scroll.ViewportExtent.Y - MathF.Max(bottomLeft.Y, topRight.Y) + offset.Y);
        Vector2 end = new(MathF.Max(bottomLeft.X, topRight.X) + offset.X,
            scroll.ViewportExtent.Y - MathF.Min(bottomLeft.Y, topRight.Y) + offset.Y);
        scroll.ScrollIntoView(start, end);
        return scroll.ScrollOffset != offset;
    }

    private bool TryFindCaretStop(ReadOnlySpan<UITextCaretStop> stops, out UITextCaretStop result)
    {
        result = default;
        if (_visualCaretLayout is not null &&
            (uint)_visualCaretIndex < (uint)stops.Length &&
            stops[_visualCaretIndex].Utf16Offset == CursorPosition &&
            ReferenceEquals(_visualCaretLayout, TextComponent.TryGetCurrentLayoutResult(
                out UITextLayoutResult currentLayout) ? currentLayout : null))
        {
            result = stops[_visualCaretIndex];
            return true;
        }
        bool found = false;
        for (int index = 0; index < stops.Length; index++)
        {
            UITextCaretStop stop = stops[index];
            if (stop.Utf16Offset != CursorPosition)
                continue;
            if (_visualCaretLayout is not null && stop.Affinity == _visualCaretAffinity)
            {
                result = stop;
                return true;
            }
            if (!found || _verticalVisualLine is null || _verticalVisualLine == stop.Line)
            {
                result = stop;
                found = true;
                if (_verticalVisualLine == stop.Line)
                    break;
            }
        }
        return found;
    }
}
