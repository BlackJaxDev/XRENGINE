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
    private float _pendingScrollY;
    private ulong _lastAppliedSequence;
    private bool _hideCursor;
    private Action<bool>? _captureRequest;

    public override Vector2 CursorPosition
    {
        get => _cursorPosition;
        set => _cursorPosition = value;
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

    public void ApplySnapshot(WindowInputSnapshot snapshot)
    {
        if (snapshot.Sequence == 0 || snapshot.Sequence == _lastAppliedSequence)
            return;

        _lastAppliedSequence = snapshot.Sequence;
        _cursorPosition = new Vector2(snapshot.PointerX, snapshot.PointerY);
        _pendingScrollY += snapshot.ScrollDeltaY;
        Array.Clear(_pressedButtons);

        ReadOnlySpan<EMouseButton> pressedButtons = snapshot.PressedMouseButtonSpan;
        for (int i = 0; i < pressedButtons.Length; i++)
        {
            int index = (int)pressedButtons[i];
            if ((uint)index < (uint)_pressedButtons.Length)
                _pressedButtons[index] = true;
        }
    }

    public override void TickStates(float delta)
    {
        TickCursorState(_cursorPosition.X, _cursorPosition.Y);

        float scrollY = _pendingScrollY;
        _pendingScrollY = 0.0f;
        if (MathF.Abs(scrollY) > float.Epsilon)
            TickScrollState(scrollY);

        TickMouseButtonState(EMouseButton.LeftClick, _pressedButtons[(int)EMouseButton.LeftClick], delta);
        TickMouseButtonState(EMouseButton.RightClick, _pressedButtons[(int)EMouseButton.RightClick], delta);
        TickMouseButtonState(EMouseButton.MiddleClick, _pressedButtons[(int)EMouseButton.MiddleClick], delta);
    }

    public override void ClearScrollBuffer()
        => _pendingScrollY = 0.0f;
}
