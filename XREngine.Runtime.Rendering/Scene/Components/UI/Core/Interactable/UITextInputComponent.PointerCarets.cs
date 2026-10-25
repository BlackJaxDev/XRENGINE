using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Places the caret and the selection from the text layout of the presented hit.</summary>
public partial class UITextInputComponent : IUIPointerMetadataSource
{
    // The press packet's layout. A drag keeps it, so a newer layout cannot move the anchor.
    private UITextCaretHitMetadata? _pointerSelection;

    /// <summary>
    /// Freezes the text layout that the same capture gives to the text render record.
    /// Returns null when that layout does not place the current text with complete
    /// grapheme stops or does not match the accepted field size.
    /// </summary>
    public object? CapturePointerMetadata(Vector2 acceptedSize)
    {
        if (_terminal || SceneNode is null || SceneNode.IsTransformNull ||
            BoundableTransform.ActualSize != acceptedSize)
            return null;
        UITextComponent text = TextComponent;
        string current = Text;
        return text.TryGetCurrentLayoutResult(out UITextLayoutResult layout) &&
            layout.HasCompleteGraphemeCaretStops &&
            string.Equals(text.Text ?? string.Empty, current, StringComparison.Ordinal)
            ? new UITextCaretHitMetadata(this, layout, current)
            : null;
    }

    public override void PointerPressed(Vector2 localPoint, in UIPointerGeometry geometry)
    {
        long revision = InteractionRevision;
        base.PointerPressed(localPoint, in geometry);
        _pointerSelection = null;
        if (!IsPressed || _terminal || IsComposing || revision != InteractionRevision ||
            geometry.Metadata is not UITextCaretHitMetadata hit ||
            !TryFindPointerCaret(hit, localPoint, out int offset))
            return;
        _pointerSelection = hit;
        PlacePointerCaret(offset, AnyShift);
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal, in UIPointerGeometry geometry)
    {
        base.MouseMoved(lastPosLocal, posLocal, in geometry);
        if (_pointerSelection is { } hit && IsPressed && !IsComposing &&
            TryFindPointerCaret(hit, posLocal, out int offset))
            PlacePointerCaret(offset, extendSelection: true);
    }

    public override void PointerReleased(Vector2 localPoint, bool inside, in UIPointerGeometry geometry)
    {
        if (_pointerSelection is { } hit && IsPressed && !IsComposing &&
            TryFindPointerCaret(hit, localPoint, out int offset))
            PlacePointerCaret(offset, extendSelection: true);
        _pointerSelection = null;
        base.PointerReleased(localPoint, inside, in geometry);
    }

    public override void PointerCancelled()
    {
        _pointerSelection = null;
        base.PointerCancelled();
    }

    /// <summary>
    /// Finds the grapheme boundary under a local point. The frozen layout must place the
    /// current text, so a layout that is pending after an edit cannot move the caret to
    /// an offset of other text.
    /// </summary>
    private bool TryFindPointerCaret(UITextCaretHitMetadata hit, Vector2 localPoint, out int offset)
    {
        offset = -1;
        if (!ReferenceEquals(hit.Owner, this) || _terminal ||
            !string.Equals(hit.Text, Text, StringComparison.Ordinal) ||
            !hit.Layout.HasCompleteGraphemeCaretStops)
            return false;
        offset = FindCaretOffsetAtPoint(hit.Layout.Carets.Span, localPoint,
            GraphemeBoundaries(), Text.Length);
        return offset >= 0;
    }

    private void PlacePointerCaret(int offset, bool extendSelection)
    {
        CursorPosition = offset;
        if (!extendSelection)
            SelectionAnchor = CursorPosition;
        EnsureCaretVisible();
    }

    /// <summary>
    /// Selects the placed line nearest to the point, then the grapheme boundary on that
    /// line nearest to the point. A line covers its caret height below its stop position.
    /// </summary>
    internal static int FindCaretOffsetAtPoint(ReadOnlySpan<UITextCaretStop> stops, Vector2 point,
        int[] graphemeBoundaries, int textLength)
    {
        float bestDistance = float.PositiveInfinity;
        int line = -1;
        for (int index = 0; index < stops.Length; index++)
        {
            UITextCaretStop stop = stops[index];
            if (stop.Utf16Offset != textLength &&
                Array.BinarySearch(graphemeBoundaries, stop.Utf16Offset) < 0)
                continue;
            float low = MathF.Min(stop.Position.Y, stop.Position.Y - stop.LineHeight);
            float high = MathF.Max(stop.Position.Y, stop.Position.Y - stop.LineHeight);
            float distance = point.Y < low ? low - point.Y : point.Y > high ? point.Y - high : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                line = stop.Line;
            }
        }
        return line < 0 ? -1 : FindNearestCaretOffset(stops, line, point.X, graphemeBoundaries, textLength);
    }
}
