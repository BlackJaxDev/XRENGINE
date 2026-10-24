using System.Numerics;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

public partial class UITextComponent
{
    private UITextLayoutResult? _acceptedLayoutResult;
    private Vector2 _acceptedLayoutSize;
    private UICanvasTransform? _acceptedLayoutCanvas;
    private UIElementIdentity _acceptedLayoutIdentity;
    private int _acceptedLayoutIndex = -1;

    /// <summary>Reports a committed canvas text result on the UI owner thread.</summary>
    public event Action<UITextComponent, UITextLayoutResult, Vector2>? AcceptedLayoutChanged;

    /// <summary>Read the last committed result without capturing new layout input.</summary>
    public bool TryGetAcceptedLayoutResult(out UITextLayoutResult result, out Vector2 actualSize)
    {
        UITextLayoutResult? accepted = _acceptedLayoutResult;
        if (accepted is not null && _acceptedLayoutCanvas is { } canvas &&
            ReferenceEquals(BoundableTransform.GetCanvasTransform(), canvas) &&
            BoundableTransform.ElementIdentity == _acceptedLayoutIdentity &&
            MatchesCurrentCanvasPageAtIndex(canvas, _acceptedLayoutIndex, accepted, _acceptedLayoutSize) &&
            IsCurrentAcceptedLayout(accepted, _acceptedLayoutSize))
        {
            result = accepted;
            actualSize = _acceptedLayoutSize;
            return true;
        }
        result = null!;
        actualSize = default;
        return false;
    }

    internal void NotifyAcceptedLayout(UITextLayoutResult result, Vector2 actualSize)
    {
        UICanvasTransform? canvas = SceneNode is null ? null : BoundableTransform.GetCanvasTransform();
        if (canvas is null || !TryFindCurrentCanvasPage(canvas, out int index))
            return;
        NotifyAcceptedLayout(result, actualSize, index);
    }

    internal void NotifyAcceptedLayout(UITextLayoutResult result, Vector2 actualSize, int index)
    {
        UICanvasTransform? canvas = SceneNode is null ? null : BoundableTransform.GetCanvasTransform();
        UIElementIdentity identity = SceneNode is null ? default : BoundableTransform.ElementIdentity;
        if (canvas is null || !MatchesCurrentCanvasPageAtIndex(canvas, index, result, actualSize) ||
            !IsCurrentAcceptedLayout(result, actualSize))
            return;
        bool duplicate = ReferenceEquals(_acceptedLayoutResult, result) && _acceptedLayoutSize == actualSize &&
            ReferenceEquals(_acceptedLayoutCanvas, canvas) && _acceptedLayoutIdentity == identity &&
            _acceptedLayoutIndex == index;
        _acceptedLayoutResult = result;
        _acceptedLayoutSize = actualSize;
        _acceptedLayoutCanvas = canvas;
        _acceptedLayoutIdentity = identity;
        _acceptedLayoutIndex = index;
        if (duplicate)
            return;
        Action<UITextComponent, UITextLayoutResult, Vector2>? handlers = AcceptedLayoutChanged;
        if (handlers is null)
            return;
        foreach (Action<UITextComponent, UITextLayoutResult, Vector2> handler in
            Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(this, result, actualSize); }
            catch (Exception error) { Debug.UIException(error); }
            if (IsDestroyed || IsDestroyQueued || SceneNode is null ||
                !ReferenceEquals(BoundableTransform.GetCanvasTransform(), canvas) ||
                BoundableTransform.ElementIdentity != identity ||
                !MatchesCurrentCanvasPageAtIndex(canvas, index, result, actualSize) ||
                !IsCurrentAcceptedLayout(result, actualSize))
                break;
        }
    }

    private bool IsCurrentAcceptedLayout(UITextLayoutResult result, Vector2 actualSize)
        => !IsDestroyed && !IsDestroyQueued && SceneNode is not null &&
            ReferenceEquals(_layoutResult, result) &&
            _layoutInput is { } input && result.TextRevision == input.TextRevision &&
            result.FontRevision == input.FontRevision &&
            ReferenceEquals(_layoutInputFont, _font) &&
            _font is { } font && font.LayoutMetricsRevision == input.FontRevision &&
            input.Text == (Text ?? string.Empty) &&
            input.FontSize == FontSize && input.Wrap == WrapMode &&
            input.AtlasSize == new Vector2(font.Atlas?.Width ?? 0, font.Atlas?.Height ?? 0) &&
            input.EmSize == font.LayoutEmSize &&
            input.HorizontalAlignment == HorizontalAlignment &&
            input.VerticalAlignment == VerticalAlignment &&
            input.Margins == BoundableTransform.Margins &&
            input.Spacing == ResolveLayoutSpacingForOutputPixels(
                OutlineAffectsSpacing ? OutlineThickness : 0.0f) &&
            input.LineSpacing == DefaultLineSpacing +
                (OutlineAffectsSpacing ? OutlineThickness : 0.0f) &&
            input.DpiScale == (BoundableTransform.GetCanvasTransform()?.DpiScale ?? 1.0f) &&
            (input.ShapingRequest is not null) == UseShapedText &&
            (!UseShapedText || input.ShapingFeatureSource == OpenTypeFeatures) &&
            (!UseShapedText || input.ShapingBackendGeneration == UITextShapingBackendRegistry.Generation) &&
            (input.ShapingRequest is not { } request ||
                request.Language == ShapeLanguage && request.Direction == ShapeDirection) &&
            _layoutResultActualSize == actualSize && BoundableTransform.ActualSize == actualSize;

    private bool TryFindCurrentCanvasPage(UICanvasTransform canvas, out int index)
    {
        ReadOnlySpan<UIElementIdentity> identities = canvas.CurrentLayoutIdentities.Span;
        UIElementIdentity identity = BoundableTransform.ElementIdentity;
        for (index = 0; index < identities.Length; index++)
            if (identities[index] == identity)
                return true;
        index = -1;
        return false;
    }

    private bool MatchesCurrentCanvasPageAtIndex(UICanvasTransform canvas, int index,
        UITextLayoutResult result, Vector2 actualSize)
    {
        UILayoutOutputPageView? pages = canvas.CurrentLayoutOutputPages;
        return pages is not null && index >= 0 && index < pages.Length &&
            index < canvas.CurrentLayoutIdentities.Length &&
            canvas.CurrentLayoutIdentities.Span[index] == BoundableTransform.ElementIdentity &&
            ReferenceEquals(pages[index].TextResult, result) && pages[index].ActualSize == actualSize;
    }

    private void RetireAcceptedLayoutObservers()
    {
        AcceptedLayoutChanged = null;
        _acceptedLayoutResult = null;
        _acceptedLayoutCanvas = null;
        _acceptedLayoutIndex = -1;
    }
}
