namespace XREngine.Input;

/// <summary>Provides optional platform input state without exposing a native windowing API.</summary>
public static class InputPlatformServices
{
    /// <summary>Reads Caps Lock state when the application has installed a platform provider.</summary>
    public static Func<bool?>? ReadCapsLockState { get; set; }

    public static bool TryGetCapsLockState(out bool enabled)
    {
        bool? state = ReadCapsLockState?.Invoke();
        enabled = state.GetValueOrDefault();
        return state.HasValue;
    }
}
