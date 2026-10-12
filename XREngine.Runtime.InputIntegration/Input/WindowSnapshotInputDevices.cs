using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

internal sealed class WindowSnapshotKeyboard(int index) : BaseKeyboard(index)
{
    private readonly bool[] _pressedKeys = new bool[(int)EKey.LastKey + 1];
    private readonly bool[] _suppressedKeys = new bool[(int)EKey.LastKey + 1];
    private readonly bool[] _transitionedKeys = new bool[(int)EKey.LastKey + 1];
    private readonly WindowSnapshotTransitionBuffer<WindowKeyTransition> _pendingTransitions = new();
    private ulong _lastAppliedSequence;
    private int _snapshotApplyDepth;
    private bool _isTicking;
    private bool _isFocused;
    private IRuntimePointerContactSource? _inputSource;
    private object? _inputSourceOwner;
    private ulong _inputSourceGeneration;

    public override ulong InputDispatchRevision
    {
        get
        {
            if (_inputSource is not null && (!ReferenceEquals(_inputSourceOwner, _inputSource.PointerContactOwner) ||
                _inputSourceGeneration != _inputSource.PointerContactGeneration))
            {
                bool ownerChanged = !ReferenceEquals(_inputSourceOwner, _inputSource.PointerContactOwner);
                _inputSource = null;
                if (ownerChanged)
                    DiscardTransientInput();
                else
                    DiscardCapturedInput();
            }
            return base.InputDispatchRevision;
        }
    }

    public void SetInputSource(IRuntimePointerContactSource? source)
    {
        object? owner = source?.PointerContactOwner;
        ulong generation = source?.PointerContactGeneration ?? 0;
        if (_inputSource is not null)
        {
            if (!ReferenceEquals(owner, _inputSourceOwner))
                DiscardTransientInput();
            else if (generation != _inputSourceGeneration)
                DiscardCapturedInput();
        }
        _inputSource = source;
        _inputSourceOwner = owner;
        _inputSourceGeneration = generation;
    }

    public bool ApplySnapshot(WindowInputSnapshot snapshot)
    {
        if (snapshot.Sequence == 0 || snapshot.Sequence == _lastAppliedSequence)
            return true;

        // A nested publication supersedes the remainder of this batch, but its fresh edges
        // remain queued for the next tick unless the input owner also changed.
        if (_isTicking || _snapshotApplyDepth > 0)
            _pendingTransitions.Clear();
        ++_snapshotApplyDepth;
        try
        {
            _lastAppliedSequence = snapshot.Sequence;
            InvalidateInputDispatch();
            _isFocused = snapshot.IsFocused;
            Array.Clear(_pressedKeys);

            ReadOnlySpan<EKey> pressedKeys = snapshot.PressedKeySpan;
            for (int i = 0; i < pressedKeys.Length; i++)
            {
                int index = (int)pressedKeys[i];
                if ((uint)index < (uint)_pressedKeys.Length)
                    _pressedKeys[index] = true;
            }

            ReadOnlySpan<WindowKeyTransition> transitions = snapshot.KeyTransitionSpan;
            if (!_pendingTransitions.TryAppend(transitions))
                DiscardTransientInput();
            ulong revision = InputDispatchRevision;
            for (int i = 0; i < transitions.Length; i++)
            {
                WindowKeyTransition transition = transitions[i];
                if (transition.Key != EKey.Unknown)
                    Keystroke(transition.Key, transition.IsDown);
                if (revision != InputDispatchRevision)
                    return false;
            }

            ReadOnlySpan<char> textInput = snapshot.TextInputCharacterSpan;
            for (int i = 0; i < textInput.Length; i++)
            {
                KeyCharacter(textInput[i]);
                if (revision != InputDispatchRevision)
                    return false;
            }
            return true;
        }
        finally { --_snapshotApplyDepth; }
    }

    public override void TickStates(float delta)
    {
        if (_isTicking)
            return;
        _isTicking = true;
        ulong revision = InputDispatchRevision;
        Array.Clear(_transitionedKeys);
        try
        {
            while (_pendingTransitions.TryDequeue(out WindowKeyTransition transition))
            {
                int index = (int)transition.Key;
                if (transition.Key == EKey.Unknown || (uint)index >= (uint)_buttonStates.Length ||
                    (uint)index >= (uint)_pressedKeys.Length)
                    continue;
                if (_suppressedKeys[index])
                {
                    if (!transition.IsDown)
                        _suppressedKeys[index] = false;
                    continue;
                }
                if (!_isFocused && transition.IsDown)
                    continue;
                _transitionedKeys[index] |= _buttonStates[index] is { } state && state.IsPressed != transition.IsDown;
                TickKeyState(transition.Key, transition.IsDown, 0.0f);
                if (revision != InputDispatchRevision)
                    return;
            }

            for (int i = 0; i < _buttonStates.Length && i < _pressedKeys.Length; i++)
            {
                if (!_pressedKeys[i])
                    _suppressedKeys[i] = false;
                else if (!_isFocused)
                    _suppressedKeys[i] = true;
                if (_buttonStates[i] is null)
                    continue;
                bool pressed = _isFocused && !_suppressedKeys[i] && _pressedKeys[i];
                // A new edge starts its hold timer here. Only unchanged channels accrue frame time.
                TickKeyState((EKey)i, pressed, _transitionedKeys[i] ? 0.0f : delta);
                if (revision != InputDispatchRevision)
                    return;
            }
        }
        finally { _isTicking = false; }
    }

    public override void DiscardTransientInput()
    {
        InvalidateInputDispatch();
        _pendingTransitions.Clear();
        for (int i = 0; i < _pressedKeys.Length; i++)
        {
            _suppressedKeys[i] |= _pressedKeys[i];
            if ((uint)i < (uint)_buttonStates.Length)
                _buttonStates[i]?.ResetStateWithoutEvents();
        }
    }

    public override void DiscardCapturedInput()
    {
        InvalidateInputDispatch();
        _pendingTransitions.Clear();
        for (int i = 0; i < _pressedKeys.Length; i++)
            if (_pressedKeys[i] && ((uint)i >= (uint)_buttonStates.Length || _buttonStates[i]?.IsPressed != true))
                _suppressedKeys[i] = true;
    }
}

internal sealed class WindowSnapshotMouse(int index) : BaseMouse(index)
{
    private readonly bool[] _pressedButtons = new bool[3];
    private readonly bool[] _suppressedButtons = new bool[3];
    private readonly bool[] _transitionedButtons = new bool[3];
    private readonly WindowSnapshotTransitionBuffer<WindowMouseButtonTransition> _pendingTransitions = new();
    private bool _isTicking;
    private bool _isFocused;
    private IRuntimePointerContactSource? _inputSource;
    private object? _inputSourceOwner;
    private ulong _inputSourceGeneration;
    private bool _suppressTouchUntilUp;
    private bool _physicalButtonTransitionsPending;
    private Vector2 _cursorPosition;
    private Vector2 _physicalCursorPosition;
    private Vector2 _touchPosition;
    private bool _touchPositionPreferred;
    private bool _touchPressed;
    private bool _touchPressPulse;
    private bool _lastTouchPressed;
    public bool IsTouchButtonDispatch { get; private set; }
    public Vector2 TouchPosition => _touchPosition;
    private float _pendingScrollY;
    private ulong _lastAppliedSequence;
    private bool _hideCursor;
    private Action<bool>? _captureRequest;

    public override ulong InputDispatchRevision
    {
        get
        {
            if (_inputSource is not null && (!ReferenceEquals(_inputSourceOwner, _inputSource.PointerContactOwner) ||
                _inputSourceGeneration != _inputSource.PointerContactGeneration))
            {
                bool ownerChanged = !ReferenceEquals(_inputSourceOwner, _inputSource.PointerContactOwner);
                _inputSource = null;
                if (ownerChanged)
                    DiscardTransientInput();
                else
                    DiscardCapturedInput();
            }
            return base.InputDispatchRevision;
        }
    }

    public void SetInputSource(IRuntimePointerContactSource? source)
    {
        object? owner = source?.PointerContactOwner;
        ulong generation = source?.PointerContactGeneration ?? 0;
        if (_inputSource is not null)
        {
            if (!ReferenceEquals(owner, _inputSourceOwner))
                DiscardTransientInput();
            else if (generation != _inputSourceGeneration)
                DiscardCapturedInput();
        }
        _inputSource = source;
        _inputSourceOwner = owner;
        _inputSourceGeneration = generation;
    }

    public override Vector2 CursorPosition
    {
        get => _cursorPosition;
        set
        {
            _cursorPosition = _physicalCursorPosition = value;
            _touchPositionPreferred = false;
        }
    }

    public override bool HideCursor
    {
        get => _hideCursor;
        set
        {
            if (_hideCursor == value)
                return;

            _hideCursor = value;
            _captureRequest?.Invoke(value);
        }
    }

    public void SetCaptureRequest(Action<bool>? captureRequest)
        => _captureRequest = captureRequest;

    public bool HasTouchButton => _touchPressed || _touchPressPulse || _lastTouchPressed;

    public void SetTouchContact(Vector2 position, bool pressed)
    {
        _touchPosition = position;
        _touchPositionPreferred = true;
        _touchPressPulse |= pressed && !_touchPressed;
        _touchPressed = pressed;
        if (!pressed)
            _suppressTouchUntilUp = false;
    }

    public void CancelTouchContact()
    {
        _touchPressed = false;
        _touchPressPulse = false;
        _suppressTouchUntilUp = false;
    }

    public void ApplySnapshot(WindowInputSnapshot snapshot)
    {
        if (snapshot.Sequence == 0 || snapshot.Sequence == _lastAppliedSequence)
            return;

        _lastAppliedSequence = snapshot.Sequence;
        if (_isTicking)
            _pendingTransitions.Clear();
        InvalidateInputDispatch();
        _isFocused = snapshot.IsFocused;
        Vector2 physicalPosition = new(snapshot.PointerX, snapshot.PointerY);
        if (physicalPosition != _physicalCursorPosition)
            _touchPositionPreferred = false;
        _physicalCursorPosition = physicalPosition;
        _cursorPosition = _touchPositionPreferred ? _touchPosition : physicalPosition;
        _pendingScrollY += snapshot.ScrollDeltaY;
        Array.Clear(_pressedButtons);

        ReadOnlySpan<EMouseButton> pressedButtons = snapshot.PressedMouseButtonSpan;
        for (int i = 0; i < pressedButtons.Length; i++)
        {
            int index = (int)pressedButtons[i];
            if ((uint)index < (uint)_pressedButtons.Length)
                _pressedButtons[index] = true;
        }
        _physicalButtonTransitionsPending |= snapshot.MouseButtonTransitionSpan.Length > 0;
        if (pressedButtons.Length > 0 || _physicalButtonTransitionsPending)
        {
            _touchPositionPreferred = false;
            _cursorPosition = _physicalCursorPosition;
        }
        if (!_pendingTransitions.TryAppend(snapshot.MouseButtonTransitionSpan))
            DiscardTransientInput();
    }

    public override void TickStates(float delta)
    {
        if (_isTicking)
            return;
        _isTicking = true;
        ulong revision = InputDispatchRevision;
        Array.Clear(_transitionedButtons);
        try
        {
            if (_physicalButtonTransitionsPending)
                _cursorPosition = _physicalCursorPosition;
            else if (_touchPositionPreferred && !_pressedButtons[0] && !_pressedButtons[1] && !_pressedButtons[2])
                _cursorPosition = _touchPosition;
            TickCursorState(_cursorPosition.X, _cursorPosition.Y);
            if (revision != InputDispatchRevision)
                return;

            float scrollY = _pendingScrollY;
            _pendingScrollY = 0.0f;
            if (MathF.Abs(scrollY) > float.Epsilon)
                TickScrollState(scrollY);
            if (revision != InputDispatchRevision)
                return;

            while (_pendingTransitions.TryDequeue(out WindowMouseButtonTransition transition))
            {
                int index = (int)transition.Button;
                if ((uint)index >= (uint)_pressedButtons.Length)
                    continue;
                if (_suppressedButtons[index])
                {
                    if (!transition.IsDown)
                        _suppressedButtons[index] = false;
                    continue;
                }
                if (!_isFocused && transition.IsDown)
                    continue;
                bool pressed = transition.IsDown || index == (int)EMouseButton.LeftClick && TouchLeft;
                _transitionedButtons[index] |= _buttonStates[index] is { } state && state.IsPressed != pressed;
                if (index == (int)EMouseButton.LeftClick)
                    TickLeftButton(transition.IsDown, 0.0f);
                else
                    TickMouseButtonState(transition.Button, pressed, 0.0f);
                if (revision != InputDispatchRevision)
                    return;
            }
            for (int index = 0; index < _pressedButtons.Length; index++)
            {
                if (!_pressedButtons[index])
                    _suppressedButtons[index] = false;
                else if (!_isFocused)
                    _suppressedButtons[index] = true;
                bool physical = _isFocused && !_suppressedButtons[index] && _pressedButtons[index];
                float buttonDelta = _transitionedButtons[index] ? 0.0f : delta;
                if (index == (int)EMouseButton.LeftClick)
                    TickLeftButton(physical, buttonDelta);
                else
                    TickMouseButtonState((EMouseButton)index, physical, buttonDelta);
                if (revision != InputDispatchRevision)
                    return;
            }
        }
        finally
        {
            _isTicking = false;
            IsTouchButtonDispatch = false;
            _touchPressPulse = false;
            if (revision == InputDispatchRevision)
                _physicalButtonTransitionsPending = false;
        }
    }

    private bool TouchLeft => !_suppressTouchUntilUp && (_touchPressed || _touchPressPulse);

    private void TickLeftButton(bool physicalLeft, float delta)
    {
        bool touchLeft = TouchLeft;
        IsTouchButtonDispatch = !physicalLeft && (touchLeft || _lastTouchPressed);
        _lastTouchPressed = !physicalLeft && touchLeft;
        try
        {
            TickMouseButtonState(EMouseButton.LeftClick, physicalLeft || touchLeft, delta);
        }
        finally
        {
            IsTouchButtonDispatch = false;
        }
    }

    public override void DiscardTransientInput()
    {
        InvalidateInputDispatch();
        _pendingTransitions.Clear();
        _physicalButtonTransitionsPending = false;
        _pendingScrollY = 0.0f;
        _suppressTouchUntilUp |= _touchPressed || _touchPressPulse;
        _touchPressPulse = false;
        for (int index = 0; index < _pressedButtons.Length; index++)
        {
            _suppressedButtons[index] |= _pressedButtons[index];
            _buttonStates[index]?.ResetStateWithoutEvents();
        }
    }

    public override void DiscardCapturedInput()
    {
        InvalidateInputDispatch();
        _pendingTransitions.Clear();
        _physicalButtonTransitionsPending = false;
        _pendingScrollY = 0.0f;
        if (_buttonStates[(int)EMouseButton.LeftClick]?.IsPressed != true)
            _suppressTouchUntilUp |= _touchPressed || _touchPressPulse;
        _touchPressPulse = false;
        for (int index = 0; index < _pressedButtons.Length; index++)
            if (_pressedButtons[index] && _buttonStates[index]?.IsPressed != true)
                _suppressedButtons[index] = true;
    }

    public override void ClearScrollBuffer()
        => _pendingScrollY = 0.0f;
}
