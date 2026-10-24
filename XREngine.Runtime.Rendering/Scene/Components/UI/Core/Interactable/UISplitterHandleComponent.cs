using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Resizes a two-pane split through a captured divider handle.</summary>
[RequiresTransform(typeof(UIBoundableTransform))]
public sealed class UISplitterHandleComponent : UIInteractableComponent
{
    private UIDualSplitTransform? _splitTarget;
    private float _minimumSize;
    private float _maximumSize = float.PositiveInfinity;
    private float _minimumOtherSize;
    private float _keyboardStep = 16;
    private long _configurationRevision = 1;
    private bool _dragging;
    private bool _updatingTarget;
    private float _pressCoordinate;
    private UISplitterPointerMetrics _dragMetrics;

    public UISplitterHandleComponent() => NeedsMouseMove = true;

    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.Splitter;
    public override string? AccessibilityValue => _splitTarget is { IsDestroyed: false } split
        ? split.FirstFixedSize.HasValue
            ? split.FixedSize?.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : split.SplitPercent.ToString("P0", System.Globalization.CultureInfo.CurrentCulture)
        : null;

    public UIDualSplitTransform? SplitTarget
    {
        get => _splitTarget;
        set
        {
            if (ReferenceEquals(_splitTarget, value))
                return;
            StopDrag();
            if (_splitTarget is not null)
                _splitTarget.PropertyChanged -= OnSplitPropertyChanged;
            if (SetField(ref _splitTarget, value))
                _configurationRevision++;
            if (_splitTarget is not null)
                _splitTarget.PropertyChanged += OnSplitPropertyChanged;
        }
    }

    public float MinimumSize
    {
        get => _minimumSize;
        set
        {
            if (!float.IsFinite(value) || value < 0 || value > MaximumSize)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _minimumSize, value))
            {
                StopDrag();
                _configurationRevision++;
            }
        }
    }

    public float MaximumSize
    {
        get => _maximumSize;
        set
        {
            if (float.IsNaN(value) || value < MinimumSize)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _maximumSize, value))
            {
                StopDrag();
                _configurationRevision++;
            }
        }
    }

    public float MinimumOtherSize
    {
        get => _minimumOtherSize;
        set
        {
            if (!float.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _minimumOtherSize, value))
            {
                StopDrag();
                _configurationRevision++;
            }
        }
    }

    public float KeyboardStep
    {
        get => _keyboardStep;
        set
        {
            if (!float.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _keyboardStep, value);
        }
    }

    public bool IsDragging => _dragging;

    protected override UIChangeDomain GetUIChangeDomain(string? propertyName)
    {
        UIChangeDomain domain = base.GetUIChangeDomain(propertyName);
        return propertyName is nameof(SplitTarget) or nameof(MinimumSize) or
            nameof(MaximumSize) or nameof(MinimumOtherSize)
            ? domain | UIChangeDomain.HitTest : domain;
    }

    /// <summary>Copies the split values that produced a presented handle hit.</summary>
    public bool TryCapturePointerGeometry(in UILayoutNodeOutput acceptedSplit,
        out UISplitterPointerMetrics metrics)
    {
        metrics = default;
        if (!CanResize() || _splitTarget is not { } split ||
            !float.IsFinite(acceptedSplit.AcceptedSplitExtent) ||
            !float.IsFinite(acceptedSplit.AcceptedSplitFirstSize) ||
            !float.IsFinite(acceptedSplit.AcceptedSplitSecondSize) ||
            acceptedSplit.AcceptedSplitExtent <= 0 ||
            acceptedSplit.AcceptedSplitFixedSide is < 0 or > 2 ||
            acceptedSplit.AcceptedSplitVertical != split.VerticalSplit ||
            acceptedSplit.AcceptedSplitFixedSide != FixedSide(split))
            return false;

        float initial = acceptedSplit.AcceptedSplitFixedSide == 2
            ? acceptedSplit.AcceptedSplitSecondSize
            : acceptedSplit.AcceptedSplitFirstSize;
        float current = acceptedSplit.AcceptedSplitFixedSide == 0
            ? split.SplitPercent * acceptedSplit.AcceptedSplitExtent
            : split.FixedSize ?? initial;
        if (!float.IsFinite(initial) || MathF.Abs(initial - current) > 0.5f)
            return false;
        metrics = new(split.ElementIdentity, _configurationRevision,
            acceptedSplit.AcceptedSplitVertical, acceptedSplit.AcceptedSplitFixedSide,
            acceptedSplit.AcceptedSplitExtent, initial);
        return true;
    }

    public override void PointerPressed(Vector2 localPoint, in UIPointerGeometry geometry)
    {
        base.PointerPressed(localPoint);
        if (geometry.Splitter is not { } metrics || !Matches(metrics))
            return;
        _pressCoordinate = metrics.Vertical ? localPoint.Y : localPoint.X;
        _dragMetrics = metrics;
        _dragging = true;
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal,
        in UIPointerGeometry geometry)
    {
        base.MouseMoved(lastPosLocal, posLocal);
        if (_dragging)
            DragTo(posLocal);
    }

    public override void PointerReleased(Vector2 localPoint, bool inside,
        in UIPointerGeometry geometry)
    {
        base.PointerReleased(localPoint, inside);
        if (_dragging)
            DragTo(localPoint);
        StopDrag();
    }

    public override void PointerCancelled()
    {
        base.PointerCancelled();
        CancelDrag();
    }

    public override void CancelForRecycle()
    {
        CancelDrag();
        SplitTarget = null;
        base.CancelForRecycle();
    }

    protected override void OnComponentDeactivated()
    {
        CancelDrag();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        CancelDrag();
        SplitTarget = null;
        base.OnDestroying();
    }

    public override bool AccessibilityAdjust(int direction)
    {
        if (direction == 0 || !CanResize() || _splitTarget is not { } split)
            return false;
        Vector4 padding = split.Padding;
        float extent = split.VerticalSplit
            ? split.ActualHeight - padding.Y - padding.W
            : split.ActualWidth - padding.X - padding.Z;
        if (!float.IsFinite(extent) || extent <= 0)
            return false;
        float current;
        if (split.FirstFixedSize.HasValue)
        {
            UIBoundableTransform? fixedChild = split.FirstFixedSize.Value ? split.First : split.Second;
            current = split.FixedSize ?? (split.VerticalSplit
                ? fixedChild?.ActualHeight ?? 0
                : fixedChild?.ActualWidth ?? 0);
        }
        else
            current = split.SplitPercent * extent;
        return SetSize(current + Math.Sign(direction) * KeyboardStep, extent);
    }

    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (_splitTarget is not { } split ||
            !float.IsFinite(direction.X) || !float.IsFinite(direction.Y))
            return false;
        float axis = split.VerticalSplit ? direction.Y : direction.X;
        if (axis == 0 || MathF.Abs(axis) <= MathF.Abs(split.VerticalSplit ? direction.X : direction.Y))
            return false;
        int sign = split.VerticalSplit ? -1 : 1;
        if (split.FirstFixedSize == false)
            sign = -sign;
        return AccessibilityAdjust(Math.Sign(axis) * sign);
    }

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Left, pressed => AdjustKey(pressed, -1, false));
        input.RegisterKeyStateChange(EKey.Right, pressed => AdjustKey(pressed, 1, false));
        input.RegisterKeyStateChange(EKey.Down, pressed => AdjustKey(pressed, -1, true));
        input.RegisterKeyStateChange(EKey.Up, pressed => AdjustKey(pressed, 1, true));
    }

    private void AdjustKey(bool pressed, int sign, bool vertical)
    {
        if (pressed && _splitTarget?.VerticalSplit == vertical)
            TryConsumeFocusDirection(vertical ? new Vector2(0, sign) : new Vector2(sign, 0));
    }

    private void DragTo(Vector2 point)
    {
        UISplitterPointerMetrics metrics = _dragMetrics;
        if (!Matches(metrics))
        {
            StopDrag();
            return;
        }
        float coordinate = metrics.Vertical ? point.Y : point.X;
        if (!float.IsFinite(coordinate))
            return;
        float sign = metrics.Vertical == (metrics.FixedSide != 2) ? -1 : 1;
        SetSize(metrics.InitialSize + (coordinate - _pressCoordinate) * sign, metrics.Extent);
    }

    private bool SetSize(float size, float extent)
    {
        if (!CanResize() || _splitTarget is not { } split || !float.IsFinite(size))
            return false;
        float upper = MathF.Max(0, MathF.Min(MaximumSize, extent - MinimumOtherSize));
        float lower = MathF.Min(MinimumSize, upper);
        size = Math.Clamp(size, lower, upper);
        _updatingTarget = true;
        try
        {
            if (split.FirstFixedSize.HasValue)
            {
                if (split.FixedSize == size)
                    return false;
                split.FixedSize = size;
            }
            else
            {
                float fraction = Math.Clamp(size / extent, 0, 1);
                if (split.SplitPercent == fraction)
                    return false;
                split.SplitPercent = fraction;
            }
            return true;
        }
        finally
        {
            _updatingTarget = false;
        }
    }

    private bool CanResize()
        => CanInteract && !IsReadOnly && _splitTarget is { IsDestroyed: false, CanUserResize: true } split &&
            split.SceneNode is { IsActiveInHierarchy: true } &&
            split.GetCanvasComponent() is { } canvas &&
            ReferenceEquals(canvas, BoundableTransform.GetCanvasComponent());

    private bool Matches(in UISplitterPointerMetrics metrics)
        => CanResize() && _splitTarget is { } split &&
            split.ElementIdentity == metrics.TargetIdentity &&
            _configurationRevision == metrics.ConfigurationRevision &&
            split.VerticalSplit == metrics.Vertical && FixedSide(split) == metrics.FixedSide;

    private void OnSplitPropertyChanged(object? sender, XREngine.Data.Core.IXRPropertyChangedEventArgs change)
    {
        if (_updatingTarget)
            return;
        if (change.PropertyName is nameof(UIDualSplitTransform.VerticalSplit) or
            nameof(UIDualSplitTransform.FirstFixedSize) or nameof(UIDualSplitTransform.FixedSize) or
            nameof(UIDualSplitTransform.SplitPercent) or nameof(UIDualSplitTransform.CanUserResize))
        {
            StopDrag();
            if (SceneNode is not null && !SceneNode.IsTransformNull)
                UITransform.RecordUIChange(UIChangeDomain.HitTest);
        }
    }

    private static int FixedSide(UIDualSplitTransform split)
        => split.FirstFixedSize switch { true => 1, false => 2, null => 0 };

    private void StopDrag()
    {
        _dragging = false;
        _dragMetrics = default;
    }

    private void CancelDrag()
    {
        if (!_dragging)
            return;
        UISplitterPointerMetrics metrics = _dragMetrics;
        StopDrag();
        if (_splitTarget is not { IsDestroyed: false } split ||
            split.ElementIdentity != metrics.TargetIdentity ||
            _configurationRevision != metrics.ConfigurationRevision ||
            split.VerticalSplit != metrics.Vertical || FixedSide(split) != metrics.FixedSide)
            return;
        _updatingTarget = true;
        try
        {
            if (metrics.FixedSide == 0)
                split.SplitPercent = Math.Clamp(metrics.InitialSize / metrics.Extent, 0, 1);
            else
                split.FixedSize = metrics.InitialSize;
        }
        finally
        {
            _updatingTarget = false;
        }
    }
}
