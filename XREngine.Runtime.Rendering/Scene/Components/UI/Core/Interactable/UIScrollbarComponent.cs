using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Drags a scroll transform through an accepted viewport and content extent.</summary>
[RequiresTransform(typeof(UIBoundableTransform))]
public class UIScrollbarComponent : UIInteractableComponent
{
    private UIScrollableTransform? _scrollTarget;
    private UIBoundableTransform? _thumb;
    private bool _horizontal;
    private bool _dragging;
    private float _grabOffset;
    private float _grabFraction;
    private float _minimumThumbPixels = 12;
    private long _configurationRevision = 1;
    private UIScrollbarPointerMetrics _dragMetrics;

    public UIScrollbarComponent() => NeedsMouseMove = true;

    public UIScrollableTransform? ScrollTarget
    {
        get => _scrollTarget;
        set
        {
            if (ReferenceEquals(_scrollTarget, value))
                return;
            _dragging = false;
            _dragMetrics = default;
            if (SetField(ref _scrollTarget, value))
                _configurationRevision++;
            UpdateThumb();
        }
    }

    public bool Horizontal
    {
        get => _horizontal;
        set
        {
            if (_horizontal == value)
                return;
            _dragging = false;
            _dragMetrics = default;
            if (SetField(ref _horizontal, value))
                _configurationRevision++;
            UpdateThumb();
        }
    }

    public float MinimumThumbPixels
    {
        get => _minimumThumbPixels;
        set
        {
            if (!float.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _minimumThumbPixels, value))
                _configurationRevision++;
            UpdateThumb();
        }
    }

    public UIBoundableTransform? ThumbTransform => _thumb;

    protected override UIChangeDomain GetUIChangeDomain(string? propertyName)
    {
        UIChangeDomain domain = base.GetUIChangeDomain(propertyName);
        return propertyName is nameof(ScrollTarget) or nameof(Horizontal) or nameof(MinimumThumbPixels)
            ? domain | UIChangeDomain.HitTest : domain;
    }

    /// <summary>Captures the accepted thumb shape for a presented pointer hit.</summary>
    public bool TryCapturePointerGeometry(Vector2 acceptedTrackSize, Vector2 acceptedThumbSize,
        Vector2 acceptedThumbTranslation, Vector2 acceptedScrollOffset,
        Vector2 acceptedMaxScrollOffset, out UIScrollbarPointerMetrics metrics)
    {
        metrics = default;
        if (_scrollTarget is not { IsDestroyed: false } target ||
            !CanScroll() || _thumb is not { IsDestroyed: false })
            return false;
        float track = _horizontal ? acceptedTrackSize.X : acceptedTrackSize.Y;
        float thumbLength = _horizontal ? acceptedThumbSize.X : acceptedThumbSize.Y;
        float thumbStart = _horizontal
            ? acceptedThumbTranslation.X
            : acceptedThumbTranslation.Y;
        float maximum = _horizontal ? acceptedMaxScrollOffset.X : acceptedMaxScrollOffset.Y;
        float offset = _horizontal ? acceptedScrollOffset.X : acceptedScrollOffset.Y;
        if (!float.IsFinite(track) || !float.IsFinite(thumbLength) || !float.IsFinite(thumbStart) ||
            !float.IsFinite(maximum) || !float.IsFinite(offset) || track <= 0 || thumbLength <= 0 ||
            thumbLength >= track || maximum <= 0)
            return false;
        float travel = track - thumbLength;
        float normalized = Math.Clamp(offset / maximum, 0, 1);
        float expectedStart = _horizontal ? normalized * travel : (1 - normalized) * travel;
        if (MathF.Abs(expectedStart - thumbStart) > 0.5f)
            return false;
        metrics = new UIScrollbarPointerMetrics(target.ElementIdentity,
            _configurationRevision, _horizontal, track, thumbStart, thumbLength, maximum);
        return true;
    }
    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.ScrollRegion;
    public override string? AccessibilityValue => _scrollTarget is { IsDestroyed: false } target
        ? (_horizontal ? target.ScrollOffset.X : target.ScrollOffset.Y)
            .ToString(System.Globalization.CultureInfo.CurrentCulture) : null;

    public override bool AccessibilityAdjust(int direction)
        => ScrollBy(Math.Sign(direction) * 48);

    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        float axis = _horizontal ? direction.X : -direction.Y;
        return float.IsFinite(axis) && axis != 0 && ScrollBy(Math.Sign(axis) * 48);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateThumb);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateThumb);
        _dragging = false;
        _dragMetrics = default;
        if (_thumb is not null)
            _thumb.Visibility = EVisibility.Collapsed;
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        _dragging = false;
        _dragMetrics = default;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateThumb);
        SceneNode? thumbNode = _thumb?.SceneNode;
        if (thumbNode is { IsDestroyed: false })
            thumbNode.Destroy(true);
        _scrollTarget = null;
        _thumb = null;
        base.OnDestroying();
    }

    public override void CancelForRecycle()
    {
        _dragging = false;
        _dragMetrics = default;
        ScrollTarget = null;
        base.CancelForRecycle();
    }

    public override void PointerPressed(Vector2 localPoint)
    {
        base.PointerPressed(localPoint);
        if (!CanScroll() || !TryGetThumbMetrics(out float track, out float thumb, out float maximum))
            return;
        float position = _horizontal ? localPoint.X : localPoint.Y;
        float start = ThumbStart(track, thumb, maximum);
        _grabOffset = position >= start && position <= start + thumb
            ? position - start : thumb * 0.5f;
        _dragging = true;
        _dragMetrics = default;
        UpdateDrag(localPoint);
    }

    public override void PointerPressed(Vector2 localPoint, in UIPointerGeometry geometry)
    {
        base.PointerPressed(localPoint);
        if (!CanScroll() || geometry.Scrollbar is not { } metrics || !MatchesMetrics(metrics))
            return;
        float position = metrics.Horizontal ? localPoint.X : localPoint.Y;
        _grabFraction = position >= metrics.ThumbStart &&
            position <= metrics.ThumbStart + metrics.ThumbLength
            ? Math.Clamp((position - metrics.ThumbStart) / metrics.ThumbLength, 0, 1)
            : 0.5f;
        _dragMetrics = metrics;
        _dragging = true;
        UpdateDrag(localPoint, metrics);
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
    {
        base.MouseMoved(lastPosLocal, posLocal);
        if (_dragging)
            UpdateDrag(posLocal);
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal, in UIPointerGeometry geometry)
    {
        base.MouseMoved(lastPosLocal, posLocal);
        if (_dragging && _dragMetrics.TrackLength > 0)
            UpdateDrag(posLocal, _dragMetrics);
    }

    public override void PointerReleased(Vector2 localPoint, bool inside)
    {
        base.PointerReleased(localPoint, inside);
        if (_dragging)
            UpdateDrag(localPoint);
        _dragging = false;
        _dragMetrics = default;
    }

    public override void PointerReleased(Vector2 localPoint, bool inside, in UIPointerGeometry geometry)
    {
        base.PointerReleased(localPoint, inside);
        if (_dragging && _dragMetrics.TrackLength > 0)
            UpdateDrag(localPoint, _dragMetrics);
        _dragging = false;
        _dragMetrics = default;
    }

    public override void PointerCancelled()
    {
        base.PointerCancelled();
        _dragging = false;
        _dragMetrics = default;
    }

    private bool ScrollBy(float pixels)
    {
        if (!CanScroll() || !float.IsFinite(pixels) || pixels == 0)
            return false;
        UIScrollableTransform target = _scrollTarget!;
        Vector2 previous = target.ScrollOffset;
        target.ScrollOffset = _horizontal
            ? previous + new Vector2(pixels, 0)
            : previous + new Vector2(0, pixels);
        UpdateThumb();
        return target.ScrollOffset != previous;
    }

    private bool CanScroll()
    {
        UIScrollableTransform? target = _scrollTarget;
        return CanInteract && !IsReadOnly && target is { IsDestroyed: false } &&
            target.SceneNode is { IsActiveInHierarchy: true } && target.Scrollable &&
            (_horizontal ? target.ScrollableX : target.ScrollableY) &&
            ReferenceEquals(target.GetCanvasComponent(), BoundableTransform.GetCanvasComponent());
    }

    private bool TryGetThumbMetrics(out float track, out float thumb, out float maximum)
    {
        UIScrollableTransform? target = _scrollTarget;
        track = _horizontal ? BoundableTransform.ActualWidth : BoundableTransform.ActualHeight;
        float viewport = target is null ? 0 : _horizontal ? target.ViewportExtent.X : target.ViewportExtent.Y;
        float content = target is null ? 0 : _horizontal ? target.ContentExtent.X : target.ContentExtent.Y;
        maximum = target is null ? 0 : _horizontal ? target.MaxScrollOffset.X : target.MaxScrollOffset.Y;
        if (!float.IsFinite(track) || !float.IsFinite(viewport) || !float.IsFinite(content) ||
            track <= 0 || viewport <= 0 || content <= viewport || maximum <= 0)
        {
            thumb = 0;
            return false;
        }
        thumb = Math.Clamp(track * viewport / content, Math.Min(_minimumThumbPixels, track), track);
        return thumb < track;
    }

    private float ThumbStart(float track, float thumb, float maximum)
    {
        float offset = _horizontal ? _scrollTarget!.ScrollOffset.X : _scrollTarget!.ScrollOffset.Y;
        float travel = track - thumb;
        float position = Math.Clamp(offset / maximum, 0, 1) * travel;
        return _horizontal ? position : travel - position;
    }

    private void UpdateDrag(Vector2 localPoint)
    {
        if (!CanScroll() || !TryGetThumbMetrics(out float track, out float thumb, out float maximum))
            return;
        float position = (_horizontal ? localPoint.X : localPoint.Y) - _grabOffset;
        float travel = track - thumb;
        float normalized = Math.Clamp(position / travel, 0, 1);
        UIScrollableTransform target = _scrollTarget!;
        Vector2 offset = target.ScrollOffset;
        if (_horizontal)
            offset.X = normalized * maximum;
        else
            offset.Y = (1 - normalized) * maximum;
        target.ScrollOffset = offset;
        UpdateThumb();
    }

    private bool MatchesMetrics(in UIScrollbarPointerMetrics metrics)
        => _scrollTarget is { IsDestroyed: false } target &&
            target.ElementIdentity == metrics.TargetIdentity &&
            _configurationRevision == metrics.ConfigurationRevision &&
            _horizontal == metrics.Horizontal;

    private void UpdateDrag(Vector2 localPoint, in UIScrollbarPointerMetrics metrics)
    {
        if (!CanScroll() || !MatchesMetrics(metrics))
        {
            _dragging = false;
            _dragMetrics = default;
            return;
        }
        float position = (metrics.Horizontal ? localPoint.X : localPoint.Y) -
            _grabFraction * metrics.ThumbLength;
        float travel = metrics.TrackLength - metrics.ThumbLength;
        if (!float.IsFinite(position) || travel <= 0)
            return;
        float normalized = Math.Clamp(position / travel, 0, 1);
        UIScrollableTransform target = _scrollTarget!;
        Vector2 offset = target.ScrollOffset;
        if (metrics.Horizontal)
            offset.X = normalized * metrics.MaximumOffset;
        else
            offset.Y = (1 - normalized) * metrics.MaximumOffset;
        target.ScrollOffset = offset;
        UpdateThumb();
    }

    private void EnsureThumb()
    {
        if (_thumb is { IsDestroyed: true })
            _thumb = null;
        if (_thumb is not null || SceneNode is null)
            return;
        SceneNode node = new(SceneNode) { Name = "Scroll thumb" };
        _thumb = node.SetTransform<UIBoundableTransform>();
        _thumb.MinAnchor = Vector2.Zero;
        _thumb.MaxAnchor = Vector2.Zero;
        _thumb.NormalizedPivot = Vector2.Zero;
        _thumb.BlocksInputBehind = false;
        node.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(ColorF4.LightGray);
    }

    private void UpdateThumb()
    {
        if (!IsActiveInHierarchy)
            return;
        EnsureThumb();
        if (_thumb is null)
            return;
        if (!CanScroll() || !TryGetThumbMetrics(out float track, out float thumb, out float maximum))
        {
            _thumb.Visibility = EVisibility.Collapsed;
            return;
        }
        if (_horizontal)
        {
            _thumb.Translation = new Vector2(ThumbStart(track, thumb, maximum), 0);
            _thumb.Width = thumb;
            _thumb.Height = BoundableTransform.ActualHeight;
        }
        else
        {
            _thumb.Translation = new Vector2(0, ThumbStart(track, thumb, maximum));
            _thumb.Width = BoundableTransform.ActualWidth;
            _thumb.Height = thumb;
        }
        _thumb.Visibility = EVisibility.Visible;
    }
}
