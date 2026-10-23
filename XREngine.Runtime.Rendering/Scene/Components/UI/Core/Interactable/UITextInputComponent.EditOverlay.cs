using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

public partial class UITextInputComponent
{
    private UIBoundableTransform? _caretOverlay;
    private readonly List<UIBoundableTransform> _selectionOverlays = [];
    private UITextLayoutResult? _overlayLayout;
    private int _overlayCaret = -1;
    private int _overlaySelectionStart = -1;
    private int _overlaySelectionEnd = -1;
    private bool _overlayTickRegistered;

    private void StartEditOverlay()
    {
        if (SceneNode is null || _overlayTickRegistered)
            return;
        _overlayTickRegistered = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateEditOverlay);
        InvalidateEditOverlay();
        UpdateEditOverlay();
    }

    private void StopEditOverlay()
    {
        if (_overlayTickRegistered)
        {
            _overlayTickRegistered = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateEditOverlay);
        }
        if (_caretOverlay is not null)
            _caretOverlay.Visibility = EVisibility.Collapsed;
        foreach (UIBoundableTransform overlay in _selectionOverlays)
            overlay.Visibility = EVisibility.Collapsed;
        InvalidateEditOverlay();
    }

    private void InvalidateEditOverlay() => _overlayLayout = null;

    private void UpdateEditOverlay()
    {
        if (!IsFocused || !IsActiveInHierarchy || SceneNode is null ||
            !TextComponent.TryGetCurrentLayoutResult(out UITextLayoutResult layout))
            return;
        if (ReferenceEquals(layout, _overlayLayout) && _overlayCaret == CursorPosition &&
            _overlaySelectionStart == SelectionStart && _overlaySelectionEnd == SelectionEnd)
            return;
        _overlayLayout = layout;
        _overlayCaret = CursorPosition;
        _overlaySelectionStart = SelectionStart;
        _overlaySelectionEnd = SelectionEnd;

        if (TryGetCaretLocalBounds(layout, out Vector4 caret))
            PlaceOverlay(ref _caretOverlay, caret, "Text caret", ColorF4.White, 20);
        else if (_caretOverlay is not null)
            _caretOverlay.Visibility = EVisibility.Collapsed;

        int used = 0;
        bool hasRun = false;
        int runLine = -1;
        Vector4 run = default;
        ReadOnlySpan<UITextCaretStop> stops = layout.Carets.Span;
        for (int i = 0; i + 1 < stops.Length; i++)
        {
            UITextCaretStop left = stops[i];
            UITextCaretStop right = stops[i + 1];
            if (left.Line != right.Line || left.Utf16Offset < SelectionStart ||
                right.Utf16Offset > SelectionEnd || right.Utf16Offset <= left.Utf16Offset)
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
            SceneNode visual = new(TextComponent.SceneNode!) { Name = name };
            overlay = visual.SetTransform<UIBoundableTransform>();
            overlay.MinAnchor = Vector2.Zero;
            overlay.MaxAnchor = Vector2.Zero;
            overlay.NormalizedPivot = Vector2.Zero;
            overlay.BlocksInputBehind = false;
            UIMaterialComponent material = visual.AddComponent<UIMaterialComponent>()!;
            XRMaterial fill = XRMaterial.CreateUnlitColorMaterialForward(color);
            fill.EnableTransparency();
            material.Material = fill;
            material.RenderCommand2D.ZIndex = zIndex;
        }
        overlay.Translation = new Vector2(bounds.X, bounds.Y);
        overlay.Width = bounds.Z;
        overlay.Height = bounds.W;
        overlay.Visibility = EVisibility.Visible;
    }
}
