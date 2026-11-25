using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

public partial class UITextInputComponent
{
    private UIBoundableTransform? _caretOverlay;
    private readonly Dictionary<UIBoundableTransform, SceneNode> _ownedEditOverlays = [];
    private readonly List<UIBoundableTransform> _selectionOverlays = [];
    private UITextLayoutResult? _overlayLayout;
    private int _overlayCaret = -1;
    private int _overlaySelectionStart = -1;
    private int _overlaySelectionEnd = -1;
    private bool _overlayTickRegistered;

    /// <summary>Gets the caret overlay key for lookup in a presented UI packet.</summary>
    public bool TryGetCaretOverlayPublicationKey(out long membershipId, out UIElementIdentity identity)
    {
        if (_terminal)
        {
            membershipId = 0;
            identity = default;
            return false;
        }
        UIBoundableTransform? overlay = _caretOverlay;
        UIMaterialComponent? material = overlay?.SceneNode?.GetComponent<UIMaterialComponent>();
        if (overlay is null || overlay.IsDestroyed || material is null || material.IsDestroyed)
        {
            membershipId = 0;
            identity = default;
            return false;
        }
        membershipId = material.RenderInfo2D.MembershipId;
        identity = overlay.ElementIdentity;
        return true;
    }

    private void StartEditOverlay()
    {
        if (_terminal || !IsActiveInHierarchy || SceneNode is null || _overlayTickRegistered)
            return;
        _overlayTickRegistered = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateEditOverlay);
        InvalidateEditOverlay();
        UpdateEditOverlay();
    }

    private void StopEditOverlay()
    {
        KeyValuePair<UIBoundableTransform, SceneNode>[]? retiredOverlays = null;
        if (_terminal)
        {
            retiredOverlays = [.. _ownedEditOverlays];
            _ownedEditOverlays.Clear();
            _caretOverlay = null;
            _selectionOverlays.Clear();
        }
        Exception? failure = null;
        if (_overlayTickRegistered)
        {
            _overlayTickRegistered = false;
            try { UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateEditOverlay); }
            catch (Exception error) { failure = error; }
        }
        if (_terminal)
        {
            if (retiredOverlays is not null)
                foreach (KeyValuePair<UIBoundableTransform, SceneNode> entry in retiredOverlays)
                    failure = RetireEditOverlay(entry.Key, entry.Value, failure);
        }
        else
        {
            if (_caretOverlay is not null)
                try { _caretOverlay.Visibility = EVisibility.Collapsed; }
                catch (Exception error) { failure = CombineLifetimeFailure(failure, error); }
            foreach (UIBoundableTransform overlay in _selectionOverlays)
                try { overlay.Visibility = EVisibility.Collapsed; }
                catch (Exception error) { failure = CombineLifetimeFailure(failure, error); }
        }
        InvalidateEditOverlay();
        if (failure is not null)
            throw failure;
    }

    private static Exception? RetireEditOverlay(UIBoundableTransform? overlay, SceneNode? owner,
        Exception? failure)
    {
        if (overlay is null)
            return failure;
        try { if (!overlay.IsDestroyed) overlay.Visibility = EVisibility.Collapsed; }
        catch (Exception error) { failure = CombineLifetimeFailure(failure, error); }
        SceneNode? node = overlay.SceneNode;
        if (node is not null && !node.IsDestroyed && !node.IsDestroyQueued &&
            ReferenceEquals(node.Parent, owner) && ReferenceEquals(node.Transform, overlay))
            try { node.Destroy(false); }
            catch (Exception error) { failure = CombineLifetimeFailure(failure, error); }
        return failure;
    }

    private void InvalidateEditOverlay() => _overlayLayout = null;

    private void UpdateEditOverlay()
    {
        if (_terminal || IsDestroyed || IsDestroyQueued || !IsFocused || !IsActiveInHierarchy || SceneNode is null ||
            !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout))
            return;
        if (ReferenceEquals(layout, _overlayLayout) && _overlayCaret == CursorPosition &&
            _overlaySelectionStart == SelectionStart && _overlaySelectionEnd == SelectionEnd)
            return;
        _overlayLayout = layout;
        _overlayCaret = CursorPosition;
        _overlaySelectionStart = SelectionStart;
        _overlaySelectionEnd = SelectionEnd;
        EnsureCaretVisible();

        if (TryGetCaretLocalBounds(layout, out Vector4 caret))
            PlaceOverlay(ref _caretOverlay, caret, "Text caret", ColorF4.White, 20);
        else if (_caretOverlay is not null)
            _caretOverlay.Visibility = EVisibility.Collapsed;

        if (!layout.HasCompleteGraphemeCaretStops)
        {
            foreach (UIBoundableTransform overlay in _selectionOverlays)
                overlay.Visibility = EVisibility.Collapsed;
            return;
        }

        int used = 0;
        bool hasRun = false;
        int runLine = -1;
        Vector4 run = default;
        ReadOnlySpan<UITextCaretStop> stops = layout.Carets.Span;
        for (int i = 0; i + 1 < stops.Length; i++)
        {
            UITextCaretStop left = stops[i];
            UITextCaretStop right = stops[i + 1];
            int firstOffset = Math.Min(left.Utf16Offset, right.Utf16Offset);
            int lastOffset = Math.Max(left.Utf16Offset, right.Utf16Offset);
            if (left.Line != right.Line || firstOffset < SelectionStart ||
                lastOffset > SelectionEnd || firstOffset == lastOffset)
                continue;
            float x = MathF.Min(left.Position.X, right.Position.X);
            float width = MathF.Abs(right.Position.X - left.Position.X);
            if (width <= 0)
                continue;
            Vector4 rect = new(x, left.Position.Y - left.LineHeight, width, left.LineHeight);
            if (hasRun && runLine == left.Line && rect.X <= run.X + run.Z + 0.5f)
                run.Z = MathF.Max(run.X + run.Z, rect.X + rect.Z) - run.X;
            else
            {
                if (hasRun)
                    PlaceSelectionOverlay(ref used, run);
                run = rect;
                runLine = left.Line;
                hasRun = true;
            }
        }
        if (hasRun)
            PlaceSelectionOverlay(ref used, run);
        for (int i = used; i < _selectionOverlays.Count; i++)
            _selectionOverlays[i].Visibility = EVisibility.Collapsed;
    }

    private void PlaceSelectionOverlay(ref int used, Vector4 bounds)
    {
        UIBoundableTransform? overlay = used < _selectionOverlays.Count ? _selectionOverlays[used] : null;
        PlaceOverlay(ref overlay, bounds, "Text selection", new ColorF4(0.2f, 0.45f, 0.9f, 0.45f), -20);
        if (used == _selectionOverlays.Count && overlay is not null)
            _selectionOverlays.Add(overlay);
        used++;
    }

    private void PlaceOverlay(ref UIBoundableTransform? overlay, Vector4 bounds,
        string name, ColorF4 color, int zIndex)
    {
        if (overlay is null)
        {
            SceneNode owner = TextComponent.SceneNode!;
            SceneNode visual = new(owner) { Name = name };
            overlay = visual.SetTransform<UIBoundableTransform>();
            _ownedEditOverlays.Add(overlay, owner);
            overlay.MinAnchor = Vector2.Zero;
            overlay.MaxAnchor = Vector2.Zero;
            overlay.NormalizedPivot = Vector2.Zero;
            overlay.BlocksInputBehind = false;
            UIMaterialComponent material = visual.AddComponent<UIMaterialComponent>()!;
            material.SetBatchSolidColor(color);
            material.RenderCommand2D.ZIndex = zIndex;
        }
        overlay.Translation = new Vector2(bounds.X, bounds.Y);
        overlay.Width = bounds.Z;
        overlay.Height = bounds.W;
        overlay.Visibility = EVisibility.Visible;
    }
}
