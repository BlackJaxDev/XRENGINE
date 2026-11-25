using System;

namespace XREngine.Rendering.UI;

/// <summary>
/// Zero-allocation state machine converting D-pad button states and analog stick axes into
/// responsive discrete navigation steps with hysteresis and hold-to-repeat.
/// </summary>
public sealed class UINavigationRepeater
{
    private const float DiagonalAxisDeadbandRatio = 0.41421356f; // tan(22.5 deg)
    private const float AxisProjectionTolerance = 0.5f;

    private bool _dpadUp;
    private bool _dpadDown;
    private bool _dpadLeft;
    private bool _dpadRight;

    private float _stickX;
    private float _stickY;
    private bool _stickEngaged;

    private EUINavigationDirection _heldDirection;
    private float _repeatTimer;

    /// <summary>
    /// Gets the direction currently held by the repeater, or <see cref="EUINavigationDirection.None"/> when neutral.
    /// </summary>
    public EUINavigationDirection HeldDirection => _heldDirection;

    /// <summary>
    /// Updates one cardinal D-pad button's pressed state.
    /// </summary>
    public void SetDPadState(EUINavigationDirection direction, bool pressed)
    {
        switch (direction)
        {
            case EUINavigationDirection.Up: _dpadUp = pressed; break;
            case EUINavigationDirection.Down: _dpadDown = pressed; break;
            case EUINavigationDirection.Left: _dpadLeft = pressed; break;
            case EUINavigationDirection.Right: _dpadRight = pressed; break;
        }
    }

    /// <summary>
    /// Updates the analog stick horizontal axis (+X right, -X left).
    /// </summary>
    public void SetStickX(float value)
        => _stickX = float.IsFinite(value) ? value : 0.0f;

    /// <summary>
    /// Updates the analog stick vertical axis (+Y up, -Y down).
    /// </summary>
    public void SetStickY(float value)
        => _stickY = float.IsFinite(value) ? value : 0.0f;

    /// <summary>
    /// Updates both analog stick axes simultaneously.
    /// </summary>
    public void SetStick(float x, float y)
    {
        SetStickX(x);
        SetStickY(y);
    }

    /// <summary>
    /// Clears all held D-pad, stick, and repeat state (for example when closing a menu).
    /// </summary>
    public void Reset()
    {
        _dpadUp = false;
        _dpadDown = false;
        _dpadLeft = false;
        _dpadRight = false;
        _stickX = 0.0f;
        _stickY = 0.0f;
        _stickEngaged = false;
        _heldDirection = EUINavigationDirection.None;
        _repeatTimer = 0.0f;
    }

    /// <summary>
    /// Evaluates immediate direction transitions without advancing hold-repeat time.
    /// Call right after a D-pad or stick input callback for zero-latency flick response.
    /// </summary>
    public EUINavigationDirection EvaluateImmediate(
        EUINavigationAllowedDirections allowedDirections,
        in UINavigationRepeatSettings settings)
        => Step(0.0f, allowedDirections, in settings);

    /// <summary>
    /// Advances hold-to-repeat timing by <paramref name="deltaSeconds"/> and returns the direction
    /// to emit on this frame, or <see cref="EUINavigationDirection.None"/> when no step should fire.
    /// </summary>
    public EUINavigationDirection Tick(
        float deltaSeconds,
        EUINavigationAllowedDirections allowedDirections,
        in UINavigationRepeatSettings settings)
        => Step(deltaSeconds > 0.0f ? deltaSeconds : 0.0f, allowedDirections, in settings);

    private EUINavigationDirection Step(
        float deltaSeconds,
        EUINavigationAllowedDirections allowedDirections,
        in UINavigationRepeatSettings settings)
    {
        EUINavigationDirection active = ResolveActiveDirection(allowedDirections, in settings);
        if (active == EUINavigationDirection.None)
        {
            _heldDirection = EUINavigationDirection.None;
            _repeatTimer = 0.0f;
            return EUINavigationDirection.None;
        }

        if (active != _heldDirection)
        {
            _heldDirection = active;
            _repeatTimer = MathF.Max(0.05f, settings.InitialRepeatDelaySeconds);
            return active;
        }

        if (!settings.AllowHoldRepeat || deltaSeconds <= 0.0f)
            return EUINavigationDirection.None;

        _repeatTimer -= deltaSeconds;
        if (_repeatTimer > 0.0f)
            return EUINavigationDirection.None;

        // Emit at most one step per frame so a frame hitch never skips multiple menu items at once.
        float interval = MathF.Max(0.03f, settings.RepeatIntervalSeconds);
        _repeatTimer = interval;
        return active;
    }

    private EUINavigationDirection ResolveActiveDirection(
        EUINavigationAllowedDirections allowedDirections,
        in UINavigationRepeatSettings settings)
    {
        // D-pad takes priority over analog stick when pressed.
        EUINavigationDirection dpad = ResolveDPadDirection(allowedDirections);
        if (dpad != EUINavigationDirection.None)
            return dpad;

        return ResolveStickDirection(allowedDirections, in settings);
    }

    private EUINavigationDirection ResolveDPadDirection(EUINavigationAllowedDirections allowedDirections)
    {
        int x = (_dpadRight ? 1 : 0) - (_dpadLeft ? 1 : 0);
        int y = (_dpadUp ? 1 : 0) - (_dpadDown ? 1 : 0);
        if (x == 0 && y == 0)
            return EUINavigationDirection.None;

        if (x != 0 && y != 0)
        {
            EUINavigationDirection diag = (x, y) switch
            {
                (-1, 1) => EUINavigationDirection.UpLeft,
                (1, 1) => EUINavigationDirection.UpRight,
                (-1, -1) => EUINavigationDirection.DownLeft,
                _ => EUINavigationDirection.DownRight,
            };
            if (UINavigationDirectionHelper.IsAllowed(diag, allowedDirections))
                return diag;
        }

        if (y != 0)
        {
            EUINavigationDirection vertical = y > 0 ? EUINavigationDirection.Up : EUINavigationDirection.Down;
            if (UINavigationDirectionHelper.IsAllowed(vertical, allowedDirections))
                return vertical;
        }

        if (x != 0)
        {
            EUINavigationDirection horizontal = x > 0 ? EUINavigationDirection.Right : EUINavigationDirection.Left;
            if (UINavigationDirectionHelper.IsAllowed(horizontal, allowedDirections))
                return horizontal;
        }

        return EUINavigationDirection.None;
    }

    private EUINavigationDirection ResolveStickDirection(
        EUINavigationAllowedDirections allowedDirections,
        in UINavigationRepeatSettings settings)
    {
        float engage = Math.Clamp(settings.StickEngageThreshold, 0.15f, 0.98f);
        float release = Math.Clamp(settings.StickReleaseThreshold, 0.05f, engage - 0.05f);

        float magSq = _stickX * _stickX + _stickY * _stickY;
        float activeThreshold = _stickEngaged ? release : engage;
        if (magSq < activeThreshold * activeThreshold)
        {
            _stickEngaged = false;
            return EUINavigationDirection.None;
        }

        _stickEngaged = true;

        float absX = MathF.Abs(_stickX);
        float absY = MathF.Abs(_stickY);

        bool allowVertical = (allowedDirections & EUINavigationAllowedDirections.Vertical) != 0;
        bool allowHorizontal = (allowedDirections & EUINavigationAllowedDirections.Horizontal) != 0;
        bool allowDiagonals = settings.AllowDiagonalStick && (allowedDirections & EUINavigationAllowedDirections.Diagonals) != 0;

        if (allowDiagonals && absX > 0.0f && absY > 0.0f)
        {
            float ratio = absX < absY ? absX / absY : absY / absX;
            if (ratio >= DiagonalAxisDeadbandRatio)
            {
                EUINavigationDirection diag = (_stickX >= 0.0f, _stickY >= 0.0f) switch
                {
                    (false, true) => EUINavigationDirection.UpLeft,
                    (true, true) => EUINavigationDirection.UpRight,
                    (false, false) => EUINavigationDirection.DownLeft,
                    (true, false) => EUINavigationDirection.DownRight,
                };
                if (UINavigationDirectionHelper.IsAllowed(diag, allowedDirections))
                    return diag;
            }
        }

        // Single-axis menus accept mildly tilted stick flicks along the allowed axis while ignoring perpendicular flicks.
        if (allowVertical && !allowHorizontal)
        {
            if (absY >= activeThreshold && absY >= absX * AxisProjectionTolerance)
            {
                EUINavigationDirection v = _stickY >= 0.0f ? EUINavigationDirection.Up : EUINavigationDirection.Down;
                return UINavigationDirectionHelper.IsAllowed(v, allowedDirections) ? v : EUINavigationDirection.None;
            }
            return EUINavigationDirection.None;
        }

        if (allowHorizontal && !allowVertical)
        {
            if (absX >= activeThreshold && absX >= absY * AxisProjectionTolerance)
            {
                EUINavigationDirection h = _stickX >= 0.0f ? EUINavigationDirection.Right : EUINavigationDirection.Left;
                return UINavigationDirectionHelper.IsAllowed(h, allowedDirections) ? h : EUINavigationDirection.None;
            }
            return EUINavigationDirection.None;
        }

        if (absY >= absX)
        {
            EUINavigationDirection v = _stickY >= 0.0f ? EUINavigationDirection.Up : EUINavigationDirection.Down;
            if (UINavigationDirectionHelper.IsAllowed(v, allowedDirections))
                return v;
        }
        else
        {
            EUINavigationDirection h = _stickX >= 0.0f ? EUINavigationDirection.Right : EUINavigationDirection.Left;
            if (UINavigationDirectionHelper.IsAllowed(h, allowedDirections))
                return h;
        }

        return EUINavigationDirection.None;
    }
}
