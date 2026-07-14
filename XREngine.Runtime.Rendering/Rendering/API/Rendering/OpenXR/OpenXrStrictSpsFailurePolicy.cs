namespace XREngine.Rendering.API.Rendering.OpenXR;

public enum OpenXrStrictSpsFailureStage
{
    None = 0,
    Capability,
    Target,
    Recording,
    LifetimeValidation,
    Submit,
    Publish,
}

public readonly record struct OpenXrStrictSpsFailureResolution(
    bool Handled,
    uint ProjectionLayerCount,
    bool SequentialFallbackRequested,
    long SequentialFallbackAttemptDelta);

public static class OpenXrStrictSpsFailurePolicy
{
    public static OpenXrStrictSpsFailureResolution Resolve(OpenXrStrictSpsFailureStage stage)
    {
        if (stage == OpenXrStrictSpsFailureStage.None)
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "A concrete strict-SPS failure stage is required.");

        return new OpenXrStrictSpsFailureResolution(
            Handled: true,
            ProjectionLayerCount: 0u,
            SequentialFallbackRequested: false,
            SequentialFallbackAttemptDelta: 0L);
    }

    internal static OpenXrStrictSpsFailureStage ResolveInjectedStage()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.OpenXrStrictSpsFailureStage);
        return Enum.TryParse(configured, ignoreCase: true, out OpenXrStrictSpsFailureStage stage)
            ? stage
            : OpenXrStrictSpsFailureStage.None;
    }

    internal static long ResolveInjectedFailureWarmupFrameCount()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.OpenXrSmokeWarmupFrames);
        return long.TryParse(configured, out long warmupFrameCount)
            ? Math.Max(0L, warmupFrameCount)
            : 0L;
    }
}
