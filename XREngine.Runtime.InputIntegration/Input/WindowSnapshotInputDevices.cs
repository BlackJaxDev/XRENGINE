using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

internal sealed class WindowSnapshotKeyboard(int index) : BaseKeyboard(index)
{
    private readonly bool[] _pressedKeys = new bool[(int)EKey.LastKey + 1];
    private ulong _lastAppliedSequence;

    public void ApplySnapshot(WindowInputSnapshot snapshot)
    {
        if (snapshot.Sequence == 0 || snapshot.Sequence == _lastAppliedSequence)
            return;

        _lastAppliedSequence = snapshot.Sequence;
        Array.Clear(_pressedKeys);

        ReadOnlySpan<EKey> pressedKeys = snapshot.PressedKeySpan;
        for (int i = 0; i < pressedKeys.Length; i++)
        {
            int index = (int)pressedKeys[i];
            if ((uint)index < (uint)_pressedKeys.Length)
                _pressedKeys[index] = true;
        }

        ReadOnlySpan<WindowKeyTransition> transitions = snapshot.KeyTransitionSpan;
        for (int i = 0; i < transitions.Length; i++)
        {
            WindowKeyTransition transition = transitions[i];
            if (transition.Key != EKey.Unknown)
                Keystroke(transition.Key, transition.IsDown);
        }

        ReadOnlySpan<char> textInput = snapshot.TextInputCharacterSpan;
        for (int i = 0; i < textInput.Length; i++)
            KeyCharacter(textInput[i]);
    }

    public override void TickStates(float delta)
    {
        for (int i = 0; i < _buttonStates.Length; i++)
        {
            if (_buttonStates[i] is null || (uint)i >= (uint)_pressedKeys.Length)
                continue;

            TickKeyState((EKey)i, _pressedKeys[i], delta);
        }
    }
}

internal sealed class WindowSnapshotMouse(int index) : BaseMouse(index)
{
    private readonly bool[] _pressedButtons = new bool[3];
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
    }

    public void CancelTouchContact()
    {
        _touchPressed = false;
        _touchPressPulse = false;
    }

    public void ApplySnapshot(WindowInputSnapshot snapshot)
    {
        if (snapshot.Sequence == 0 || snapshot.Sequence == _lastAppliedSequence)
            return;

        _lastAppliedSequence = snapshot.Sequence;
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
        if (pressedButtons.Length > 0)
        {
            _touchPositionPreferred = false;
            _cursorPosition = _physicalCursorPosition;
        }
    }

    public override void TickStates(float delta)
    {
        if (_touchPositionPreferred && !_pressedButtons[0] && !_pressedButtons[1] && !_pressedButtons[2])
            _cursorPosition = _touchPosition;
        TickCursorState(_cursorPosition.X, _cursorPosition.Y);

        float scrollY = _pendingScrollY;
        _pendingScrollY = 0.0f;
        if (MathF.Abs(scrollY) > float.Epsilon)
            TickScrollState(scrollY);

        bool physicalLeft = _pressedButtons[(int)EMouseButton.LeftClick];
        bool touchLeft = _touchPressed || _touchPressPulse;
        IsTouchButtonDispatch = !physicalLeft && (touchLeft || _lastTouchPressed);
        _lastTouchPressed = !physicalLeft && touchLeft;
        try
        {
            TickMouseButtonState(EMouseButton.LeftClick, physicalLeft || touchLeft, delta);
        }
        finally
        {
            IsTouchButtonDispatch = false;
            _touchPressPulse = false;
        }
        TickMouseButtonState(EMouseButton.RightClick, _pressedButtons[(int)EMouseButton.RightClick], delta);
        TickMouseButtonState(EMouseButton.MiddleClick, _pressedButtons[(int)EMouseButton.MiddleClick], delta);
    }

    public override void ClearScrollBuffer()
        => _pendingScrollY = 0.0f;
}
