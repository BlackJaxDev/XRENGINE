namespace XREngine.Rendering.UI;

public partial class UITextInputComponent
{
    private UITextLayoutResult? _visualCaretLayout;
    private int _visualCaretIndex = -1;
    private UITextCaretAffinity _visualCaretAffinity = UITextCaretAffinity.Downstream;

    /// <summary>Report when shaped text has no stop at each grapheme boundary.</summary>
    public string? CaretEditingDiagnostic
    {
        get
        {
            if (SceneNode is null || SceneNode.IsTransformNull)
                return null;
            if (!TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout))
                return TextComponent.UseShapedText ? "UI.ShapedCaretLayoutUnavailable" : null;
            return layout.UsesShapedGlyphs && !layout.HasCompleteGraphemeCaretStops
                ? "UI.ShapedCaretStopsIncomplete" : null;
        }
    }

    private bool TryMoveCaretByGrapheme(int direction, bool extendSelection = false)
    {
        if (direction == 0 || IsComposing)
            return false;
        if (SceneNode is not null && !SceneNode.IsTransformNull)
        {
            if (TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout))
            {
                if (!layout.HasCompleteGraphemeCaretStops)
                    return false;
                if (layout.UsesShapedGlyphs)
                    return TextComponent.Text == Text &&
                        TryMoveVisualCaret(layout, direction, extendSelection);
            }
            else if (TextComponent.UseShapedText)
                return false;
        }

        int next = direction < 0 ? PreviousGraphemeBoundary(CursorPosition) :
            NextGraphemeBoundary(CursorPosition);
        if (next == CursorPosition)
            return false;
        CursorPosition = next;
        if (!extendSelection)
            SelectionAnchor = CursorPosition;
        EnsureCaretVisible();
        return true;
    }

    private bool TryMoveVisualCaret(UITextLayoutResult layout, int direction, bool extendSelection)
    {
        ReadOnlySpan<UITextCaretStop> stops = layout.Carets.Span;
        int current = -1;
        if (ReferenceEquals(_visualCaretLayout, layout) &&
            (uint)_visualCaretIndex < (uint)stops.Length &&
            stops[_visualCaretIndex].Utf16Offset == CursorPosition)
            current = _visualCaretIndex;
        else
            for (int index = 0; index < stops.Length; index++)
                if (stops[index].Utf16Offset == CursorPosition)
                {
                    if (current < 0)
                        current = index;
                    if (stops[index].Affinity == _visualCaretAffinity)
                    {
                        current = index;
                        break;
                    }
                }
        if (current < 0)
            return false;

        int next = FindNextVisualCaretIndex(stops, current, direction,
            CursorPosition, GraphemeBoundaries(), Text.Length);
        if (next < 0)
            return false;
        CursorPosition = stops[next].Utf16Offset;
        _visualCaretLayout = layout;
        _visualCaretIndex = next;
        _visualCaretAffinity = stops[next].Affinity;
        if (!extendSelection)
            SelectionAnchor = CursorPosition;
        EnsureCaretVisible();
        return true;
    }

    internal static int FindNextVisualCaretIndex(ReadOnlySpan<UITextCaretStop> stops,
        int currentIndex, int direction, int currentOffset, int[] graphemeBoundaries,
        int textLength)
    {
        if ((uint)currentIndex >= (uint)stops.Length || direction == 0)
            return -1;
        for (int index = currentIndex + Math.Sign(direction);
             (uint)index < (uint)stops.Length; index += Math.Sign(direction))
        {
            int offset = stops[index].Utf16Offset;
            if (offset != currentOffset && (offset == textLength ||
                Array.BinarySearch(graphemeBoundaries, offset) >= 0))
                return index;
        }
        return -1;
    }
}
