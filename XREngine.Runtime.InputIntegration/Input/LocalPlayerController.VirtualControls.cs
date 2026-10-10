using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

public partial class LocalPlayerController
{
    private readonly WindowPointerContact[] _contactEvents = new WindowPointerContact[WindowPointerContactBuffer.Capacity];
    private readonly VirtualInputCapture[] _virtualCaptures = new VirtualInputCapture[WindowPointerContactBuffer.MaximumContacts + 1];
    private readonly XRComponent[] _canvasInputScratch = new XRComponent[16];
    private readonly int[] _virtualPressCounts = new int[14];
    private WindowInputSnapshot _latestInputSnapshot;
    private object? _contactOwner;
    private ulong _contactGeneration;
    private object? _contactCutoffOwner;
    private ulong _contactCutoffSequence;
    private uint _contactRoutingRevision;
    private ulong _mouseTransitionSequence;
    private int _touchMouseId = -1;
    private bool _mouseDown;
    private bool _ignoreMouseUntilUp;
    private int _contactViewportWidth;
    private int _contactViewportHeight;
    private int _contactViewportX;
    private int _contactViewportY;

    internal bool IsVirtualGamepadDispatch => _snapshotGamepad.IsVirtualDispatch;
    internal bool VirtualMouseConsumedThisFrame { get; private set; }
    internal bool TouchMouseCancellationPending { get; private set; }
    internal bool IsTouchMouseDispatch => _snapshotMouse.IsTouchButtonDispatch;
    internal Vector2 TouchMousePosition => _snapshotMouse.TouchPosition;

    private void UpdateVirtualInput(bool capturedByUi)
    {
        VirtualMouseConsumedThisFrame = FindCapture(-1) >= 0;
        Array.Clear(_virtualPressCounts);
        for (int index = 0; index < _virtualCaptures.Length; index++)
            _virtualCaptures[index].BeganThisFrame = false;

        if (_index != ELocalPlayerIndex.One)
        {
            CancelVirtualInput();
            return;
        }

        int eventCount = 0;
        IRuntimeLocalPlayerViewport? replayViewport = _viewport;
        BoundingRectangle? replayRegion = (_viewport as XRViewport)?.Region;
        IRuntimePointerContactSource? source = _viewport as IRuntimePointerContactSource;
        object? sourceOwner = source?.PointerContactOwner;
        ulong sourceGeneration = 0;
        if (source is not null)
        {
            eventCount = source.ConsumePointerContacts(_contactEvents, out sourceGeneration);
            if (!ReferenceEquals(sourceOwner, _contactOwner) || sourceGeneration != _contactGeneration)
            {
                uint expectedRevision = _contactRoutingRevision + 1;
                CancelVirtualInput();
                if (_contactRoutingRevision != expectedRevision ||
                    !IsContactReplayCurrent(replayViewport, replayRegion, source, sourceOwner, sourceGeneration))
                    return;
                _contactOwner = sourceOwner;
                _contactGeneration = sourceGeneration;
            }
        }
        if (_viewport is XRViewport viewport &&
            (viewport.Width != _contactViewportWidth || viewport.Height != _contactViewportHeight ||
             viewport.Region.X != _contactViewportX || viewport.Region.Y != _contactViewportY))
        {
            uint expectedRevision = _contactRoutingRevision + 1;
            CancelVirtualInput();
            if (_contactRoutingRevision != expectedRevision ||
                !IsContactReplayCurrent(replayViewport, replayRegion, source, sourceOwner, sourceGeneration))
                return;
            _contactViewportWidth = viewport.Width;
            _contactViewportHeight = viewport.Height;
            _contactViewportX = viewport.Region.X;
            _contactViewportY = viewport.Region.Y;
        }

        // The host's primary stream is deliberately not distributed to additional local players.
        if (capturedByUi || !_latestInputSnapshot.IsFocused || _viewport is null ||
            _index != ELocalPlayerIndex.One || _controlledPawn is not IRuntimeInputControllablePawn pawn ||
            !ReferenceEquals(pawn.Controller, this))
        {
            CancelVirtualInput();
            return;
        }

        uint routingRevision = _contactRoutingRevision;
        for (int index = 0; index < eventCount; index++)
        {
            WindowPointerContact contact = _contactEvents[index];
            if (ReferenceEquals(_contactOwner, _contactCutoffOwner) && contact.Sequence <= _contactCutoffSequence)
                continue;
            Vector2 position = new(contact.X, contact.Y);
            int capture = FindCapture(contact.Id);
            switch (contact.Phase)
            {
                case EPointerContactPhase.Began:
                    if (capture < 0 && TryBeginVirtualContact(contact.Id, position, pawn))
                        break;
                    if (_touchMouseId < 0 && capture < 0)
                    {
                        _touchMouseId = contact.Id;
                        _snapshotMouse.SetTouchContact(position, true);
                    }
                    break;
                case EPointerContactPhase.Moved:
                    if (capture >= 0)
                        MoveCapture(capture, position);
                    else if (_touchMouseId == contact.Id)
                        _snapshotMouse.SetTouchContact(position, true);
                    break;
                case EPointerContactPhase.Ended:
                case EPointerContactPhase.Cancelled:
                    bool cancelled = contact.Phase == EPointerContactPhase.Cancelled;
                    if (capture >= 0)
                        ReleaseCapture(capture, cancelled);
                    else if (_touchMouseId == contact.Id)
                    {
                        _touchMouseId = -1;
                        if (cancelled)
                            CancelTouchMouse();
                        else
                            _snapshotMouse.SetTouchContact(position, false);
                    }
                    break;
            }
            if (routingRevision != _contactRoutingRevision ||
                !IsContactReplayCurrent(replayViewport, replayRegion, source, sourceOwner, sourceGeneration))
            {
                CancelVirtualInput();
                return;
            }
        }

        UpdateVirtualMouse(pawn);
        if (routingRevision != _contactRoutingRevision ||
            !IsContactReplayCurrent(replayViewport, replayRegion, source, sourceOwner, sourceGeneration))
        {
            CancelVirtualInput();
            return;
        }
        PublishVirtualGamepad();
    }

    private bool IsContactReplayCurrent(IRuntimeLocalPlayerViewport? viewport, BoundingRectangle? region,
        IRuntimePointerContactSource? source, object? owner, ulong generation)
        => ReferenceEquals(_viewport, viewport) &&
            (region is null || viewport is XRViewport renderViewport && renderViewport.Region.Equals(region.Value)) &&
            (source is null || ReferenceEquals(source.PointerContactOwner, owner) && source.PointerContactGeneration == generation);

    private void UpdateVirtualMouse(IRuntimeInputControllablePawn pawn)
    {
        Vector2 position = new(_latestInputSnapshot.PointerX, _latestInputSnapshot.PointerY);
        bool down = false;
        foreach (EMouseButton button in _latestInputSnapshot.PressedMouseButtonSpan)
            down |= button == EMouseButton.LeftClick;
        if (_ignoreMouseUntilUp)
        {
            _ignoreMouseUntilUp = down;
            _mouseDown = down;
            return;
        }
        if (_mouseTransitionSequence != _latestInputSnapshot.Sequence)
        {
            _mouseTransitionSequence = _latestInputSnapshot.Sequence;
            foreach (WindowMouseButtonTransition transition in _latestInputSnapshot.MouseButtonTransitionSpan)
            {
                if (transition.Button != EMouseButton.LeftClick)
                    continue;
                ApplyVirtualMouseButton(transition.IsDown, position, pawn);
            }
        }
        if (_mouseDown != down)
            ApplyVirtualMouseButton(down, position, pawn);
        int capture = FindCapture(-1);
        if (capture >= 0)
            MoveCapture(capture, position);
    }

    private void ApplyVirtualMouseButton(bool down, Vector2 position, IRuntimeInputControllablePawn pawn)
    {
        if (_mouseDown == down)
            return;
        _mouseDown = down;
        if (down)
        {
            VirtualMouseConsumedThisFrame |= TryBeginVirtualContact(-1, position, pawn);
            return;
        }
        int capture = FindCapture(-1);
        if (capture >= 0)
        {
            VirtualMouseConsumedThisFrame = true;
            ReleaseCapture(capture, false);
        }
    }

    // True also means a busy virtual control or a bounded-buffer overflow: neither may fall through to a mouse click.
    private bool TryBeginVirtualContact(int id, Vector2 position, IRuntimeInputControllablePawn pawn)
    {
        int count = pawn.LinkedUiInputComponents.Count;
        if (count > _canvasInputScratch.Length)
            return true;
        pawn.LinkedUiInputComponents.CopyTo(_canvasInputScratch, 0);
        try
        {
            for (int index = count - 1; index >= 0; index--)
            {
                if (_canvasInputScratch[index] is not UICanvasInputComponent canvas || !canvas.CanRouteVirtualInput(this))
                    continue;
                UIVirtualInputComponent? control = canvas.FindVirtualControl(position, out Vector2 local, out bool overflow, out bool hasUiHit);
                if (overflow)
                    return true;
                if (control is null)
                {
                    if (hasUiHit)
                        return false;
                    continue;
                }
                int slot = FindFreeCapture();
                if (slot < 0 || !control.BeginPointer(this, local))
                    return true;
                _virtualCaptures[slot] = new VirtualInputCapture
                {
                    PointerId = id, Canvas = canvas, Control = control, Position = position,
                    BeginButtonMask = control.ButtonMask, BeganThisFrame = true,
                };
                AddButtonPulse(control.ButtonMask, 1);
                // Virtual controls are gameplay surfaces, not keyboard/text focus targets.
                canvas.FocusedComponent = null;
                return true;
            }
        }
        finally { Array.Clear(_canvasInputScratch, 0, count); }
        return false;
    }

    private void MoveCapture(int index, Vector2 position)
    {
        ref VirtualInputCapture capture = ref _virtualCaptures[index];
        if (capture.Control is not { } control || capture.Canvas is not { } canvas ||
            !control.IsCapturedBy(this) || !control.IsActiveInHierarchy || !control.UITransform.IsVisibleInHierarchy ||
            !canvas.CanRouteVirtualInput(this) || !ReferenceEquals(control.UserInterfaceCanvas, canvas.GetCameraCanvas()) ||
            !canvas.TryGetContactCoordinate(position, false, out Vector2 canvasPosition))
        {
            ReleaseCapture(index, true);
            return;
        }
        capture.Position = position;
        control.UpdatePointer(control.UITransform.CanvasToLocal(canvasPosition));
    }

    private void ReleaseCapture(int index, bool cancelled)
    {
        ref VirtualInputCapture capture = ref _virtualCaptures[index];
        if (cancelled && capture.BeganThisFrame)
            AddButtonPulse(capture.BeginButtonMask, -1);
        capture.Control?.EndPointer(this);
        capture = default;
    }

    private int FindCapture(int id)
    {
        for (int index = 0; index < _virtualCaptures.Length; index++)
            if (_virtualCaptures[index].Control is not null && _virtualCaptures[index].PointerId == id)
                return index;
        return -1;
    }

    private int FindFreeCapture()
    {
        for (int index = 0; index < _virtualCaptures.Length; index++)
            if (_virtualCaptures[index].Control is null)
                return index;
        return -1;
    }

    private void AddButtonPulse(ushort mask, int delta)
    {
        for (int index = 0; index < _virtualPressCounts.Length; index++)
            if ((mask & (1 << index)) != 0)
                _virtualPressCounts[index] += delta;
    }

    private void PublishVirtualGamepad()
    {
        Span<float> axes = stackalloc float[6];
        axes.Clear();
        ushort buttons = 0;
        bool connected = false;
        for (int index = 0; index < _virtualCaptures.Length; index++)
        {
            if (_virtualCaptures[index].Control is not null)
                MoveCapture(index, _virtualCaptures[index].Position);
            if (_virtualCaptures[index].Control is not { } control)
                continue;
            connected = true;
            buttons |= control.ButtonMask;
            control.WriteAxes(axes);
        }
        for (int index = 0; index < _virtualPressCounts.Length; index++)
            if (_virtualPressCounts[index] > 0)
                buttons |= (ushort)(1 << index);
        _snapshotGamepad.SetVirtualSnapshot(new WindowGamepadSnapshot(connected || buttons != 0, buttons,
            axes[0], axes[1], axes[2], axes[3], axes[4], axes[5]));
    }

    private void CancelTouchMouse()
    {
        TouchMouseCancellationPending |= _touchMouseId >= 0 || _snapshotMouse.HasTouchButton;
        _touchMouseId = -1;
        _snapshotMouse.CancelTouchContact();
    }

    private void CancelVirtualInput()
    {
        CancelVirtualControls();
        CancelTouchMouse();
    }

    private void CancelVirtualControls()
    {
        ++_contactRoutingRevision;
        for (int index = 0; index < _virtualCaptures.Length; index++)
            ReleaseCapture(index, true);
        Array.Clear(_virtualPressCounts);
        _snapshotGamepad.CancelVirtualInput();
        _ignoreMouseUntilUp = true;
    }

    private void MarkContactOwnershipBoundary()
    {
        if (_viewport is not IRuntimePointerContactSource source)
            return;
        _contactCutoffOwner = source.PointerContactOwner;
        _contactCutoffSequence = source.PointerContactSequence;
    }
}
