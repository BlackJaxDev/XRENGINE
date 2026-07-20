namespace XREngine.Input;

/// <summary>
/// Provides frame input state to runtime components without coupling them to the engine orchestrator.
/// </summary>
public interface IRuntimeInputServices
{
    /// <summary>
    /// Gets the time-dilated update delta used to dispatch frame input.
    /// </summary>
    float UpdateDeltaSeconds { get; }

    /// <summary>
    /// Gets whether the editor or runtime UI currently owns input focus.
    /// </summary>
    bool IsUIInputCaptured { get; }
}

/// <summary>
/// Process-wide runtime input service seam.
/// </summary>
public static class RuntimeInputServices
{
    private sealed class DefaultRuntimeInputServices : IRuntimeInputServices
    {
        public float UpdateDeltaSeconds => 0.0f;
        public bool IsUIInputCaptured => false;
    }

    private static IRuntimeInputServices _current = new DefaultRuntimeInputServices();

    public static IRuntimeInputServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}