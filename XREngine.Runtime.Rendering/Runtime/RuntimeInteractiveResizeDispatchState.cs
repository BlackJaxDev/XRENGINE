namespace XREngine.Rendering;

/// <summary>
/// Latches modal interactive-resize dispatch ownership for the collapsed
/// window/render thread. The live native resize flag may change while a callback
/// is unwinding; this scope remains authoritative for the complete render dispatch.
/// </summary>
public static class RuntimeInteractiveResizeDispatchState
{
    [ThreadStatic]
    private static int _depth;

    /// <summary>Whether the current thread is executing a modal resize render dispatch.</summary>
    public static bool IsActive => _depth > 0;

    /// <summary>Begins one nested modal resize render dispatch.</summary>
    public static void Enter() => _depth++;

    /// <summary>Ends the current modal resize render dispatch.</summary>
    public static void Exit()
    {
        if (_depth <= 0)
            throw new InvalidOperationException(
                "Interactive resize render-dispatch scope underflowed.");

        _depth--;
    }
}
