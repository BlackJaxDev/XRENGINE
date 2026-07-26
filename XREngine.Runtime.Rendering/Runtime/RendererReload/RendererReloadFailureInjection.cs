using System.ComponentModel;

namespace XREngine.Rendering;

[Flags]
public enum RendererReloadInjectedFailure
{
    None = 0,
    ShaderCompile = 1 << 0,
    ProgramLink = 1 << 1,
    BackendBuild = 1 << 2,
    ShadowCopy = 1 << 3,
    ModuleValidation = 1 << 4,
    GpuDrain = 1 << 5,
    WorkerShutdown = 1 << 6,
    CallbackStillRegistered = 1 << 7,
    ResourceLeak = 1 << 8,
    CandidateInitialization = 1 << 9,
    FirstFrame = 1 << 10,
    Rollback = 1 << 11,
    DeviceLoss = 1 << 12,
    DelayedCompletion = 1 << 13,
    UnloadLeak = 1 << 14,
}

/// <summary>
/// Explicit test hook for deterministic renderer reload failure and race coverage.
/// It is inert unless a test opts in.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RendererReloadFailureInjection
{
    private static int _failures;
    private static int _delayMilliseconds;

    public static RendererReloadInjectedFailure Failures
    {
        get => (RendererReloadInjectedFailure)Volatile.Read(ref _failures);
        set => Volatile.Write(ref _failures, (int)value);
    }

    public static int DelayMilliseconds
    {
        get => Volatile.Read(ref _delayMilliseconds);
        set => Volatile.Write(ref _delayMilliseconds, Math.Clamp(value, 0, 30000));
    }

    public static bool IsEnabled(RendererReloadInjectedFailure failure)
        => (Failures & failure) != 0;

    public static void ThrowIfEnabled(
        RendererReloadInjectedFailure failure,
        string phase)
    {
        if (IsEnabled(failure))
            throw new RendererReloadInjectedException(failure, phase);
    }

    public static void DelayIfEnabled(RendererReloadInjectedFailure failure)
    {
        if (!IsEnabled(failure))
            return;

        int delay = DelayMilliseconds;
        if (delay > 0)
            Thread.Sleep(delay);
    }

    public static void Reset()
    {
        Failures = RendererReloadInjectedFailure.None;
        DelayMilliseconds = 0;
    }
}

public sealed class RendererReloadInjectedException : Exception
{
    public RendererReloadInjectedException(
        RendererReloadInjectedFailure failure,
        string phase)
        : base($"Injected renderer reload failure '{failure}' during '{phase}'.")
    {
        Failure = failure;
        Phase = phase;
    }

    public RendererReloadInjectedFailure Failure { get; }

    public string Phase { get; }
}
