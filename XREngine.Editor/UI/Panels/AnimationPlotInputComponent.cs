using System.Numerics;
using XREngine.Animation;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Routes plot input using the geometry that produced the accepted hit.</summary>
internal sealed class AnimationPlotInputComponent : UIInteractableComponent,
    IUIPointerMetadataSource
{
    private AnimationPlotPointerSnapshot? _pressedSnapshot;
    private Vector2 _pressPoint;
    private bool _dragged;
    private AnimationClip? _lastClickClip;
    private AnimationMember? _lastClickMember;
    private ulong _lastClickSourceRevision;
    private ulong _lastClickViewRevision;
    private Vector2 _lastClickPoint;
    private DateTime _lastClickTime;
    private ulong _dispatchGeneration;
    private static readonly InvalidOperationException StalePanelAssignment =
        new("Plot owner changed during assignment.");

    internal ulong DispatchGeneration => _dispatchGeneration;

    private AnimationPanel? _panel;
    public AnimationPanel? Panel
    {
        get => _panel;
        set
        {
            if (ReferenceEquals(_panel, value))
                return;
            AnimationPanel? previous = _panel;
            ClearPointerOwner();
            if (!ReferenceEquals(_panel, previous))
                return;
            ulong generation = _dispatchGeneration;
            try
            {
                SetFieldValidated(ref _panel, value, (Owner: this, Generation: generation),
                    static (state, original, current, _) =>
                    {
                        if (state.Owner._dispatchGeneration != state.Generation ||
                            !ReferenceEquals(original, current))
                            throw StalePanelAssignment;
                    });
            }
            catch (InvalidOperationException exception)
                when (ReferenceEquals(exception, StalePanelAssignment)) { }
        }
    }

    public AnimationPlotInputComponent()
    {
        RegisterInputsOnFocus = false;
        IsTabStop = false;
        NeedsMouseMove = true;
        AccessibilityLabel = "Animation plot";
    }

    public object? CapturePointerMetadata(Vector2 acceptedSize)
        => Panel?.CapturePlotPointerMetadata(acceptedSize);

    public override void PointerPressed(Vector2 localPoint, in UIPointerGeometry geometry)
    {
        ulong generation = ++_dispatchGeneration;
        AnimationPanel? panel = Panel;
        long revision = InteractionRevision;
        base.PointerPressed(localPoint, in geometry);
        if (!IsPressed || !HasSameOwner(panel, revision, generation) ||
            geometry.Metadata is not AnimationPlotPointerSnapshot snapshot ||
            !ReferenceEquals(snapshot.PanelOwner, panel) ||
            !ReferenceEquals(snapshot.InputOwner, this))
            return;
        _pressedSnapshot = snapshot;
        _pressPoint = localPoint;
        _dragged = false;
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal,
        in UIPointerGeometry geometry)
    {
        ulong generation = ++_dispatchGeneration;
        AnimationPanel? panel = Panel;
        long revision = InteractionRevision;
        base.MouseMoved(lastPosLocal, posLocal, in geometry);
        if (!HasSameOwner(panel, revision, generation))
            return;
        if (_pressedSnapshot is not { } snapshot || !IsPressed)
            return;
        if (!_dragged && Vector2.DistanceSquared(_pressPoint, posLocal) < 16)
            return;
        _dragged = true;
        panel?.PanPlotFromPointer(snapshot, _pressPoint, posLocal);
    }

    public override void PointerReleased(Vector2 localPoint, bool inside,
        in UIPointerGeometry geometry)
    {
        ulong generation = ++_dispatchGeneration;
        AnimationPlotPointerSnapshot? snapshot = _pressedSnapshot;
        AnimationPanel? panel = Panel;
        long revision = InteractionRevision;
        _pressedSnapshot = null;
        base.PointerReleased(localPoint, inside, in geometry);
        if (!HasSameOwner(panel, revision, generation))
            return;
        if (_dragged)
        {
            if (snapshot is not null)
                panel?.CompletePlotDrag(snapshot);
            return;
        }
        if (!inside || snapshot is null ||
            !ReferenceEquals(snapshot, geometry.Metadata) ||
            !snapshot.TryGetLane(localPoint, out AnimationMember? member) ||
            member is null)
            return;
        DateTime now = DateTime.UtcNow;
        bool doubleClick = ReferenceEquals(_lastClickClip, snapshot.Clip) &&
            ReferenceEquals(_lastClickMember, member) &&
            _lastClickSourceRevision == snapshot.SourceRevision &&
            _lastClickViewRevision == snapshot.ViewRevision &&
            now - _lastClickTime <= TimeSpan.FromMilliseconds(450) &&
            Vector2.DistanceSquared(_lastClickPoint, localPoint) <= 64;
        panel?.ActivatePlotPointer(snapshot, member, localPoint, doubleClick);
        if (!HasSameOwner(panel, revision, generation))
            return;
        _lastClickClip = snapshot.Clip;
        _lastClickMember = member;
        _lastClickSourceRevision = snapshot.SourceRevision;
        _lastClickViewRevision = snapshot.ViewRevision;
        _lastClickPoint = localPoint;
        _lastClickTime = now;
    }

    public override void PointerCancelled()
    {
        ClearPointerOwner();
        base.PointerCancelled();
    }

    protected override void OnComponentDeactivated()
    {
        ClearPointerOwner();
        base.OnComponentDeactivated();
    }

    private bool HasSameOwner(AnimationPanel? panel, long revision,
        ulong generation)
        => panel is not null && ReferenceEquals(Panel, panel) &&
            !IsDestroyed && IsActiveInHierarchy && InteractionRevision == revision &&
            _dispatchGeneration == generation;

    private void ClearPointerOwner()
    {
        _dispatchGeneration++;
        AnimationPlotPointerSnapshot? snapshot = _pressedSnapshot;
        _pressedSnapshot = null;
        _lastClickClip = null;
        if (snapshot is not null)
            _panel?.CancelPlotDrag(snapshot);
    }
}
