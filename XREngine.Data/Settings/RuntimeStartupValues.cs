namespace XREngine;

/// <summary>
/// Runtime-safe scalar startup values. Application composition roots may derive
/// these values from authored settings without exposing renderer, input, editor,
/// or native-window implementation types to lower assemblies.
/// </summary>
public readonly record struct RuntimeStartupValues(
    float? TargetUpdatesPerSecond,
    float FixedFramesPerSecond,
    float? TargetFramesPerSecond,
    float? UnfocusedTargetFramesPerSecond,
    bool LogOutputToFile,
    bool RunWithoutWindows,
    bool GpuRenderDispatch)
{
    /// <summary>Stable runtime defaults used when an application supplies no authored values.</summary>
    public static RuntimeStartupValues Default { get; } = new(
        TargetUpdatesPerSecond: 90.0f,
        FixedFramesPerSecond: 90.0f,
        TargetFramesPerSecond: 90.0f,
        UnfocusedTargetFramesPerSecond: null,
        LogOutputToFile: true,
        RunWithoutWindows: false,
        GpuRenderDispatch: false);

    /// <summary>Stable headless defaults which never request local presentation.</summary>
    public static RuntimeStartupValues HeadlessServer { get; } = Default with
    {
        RunWithoutWindows = true,
        TargetFramesPerSecond = null,
        UnfocusedTargetFramesPerSecond = null,
    };
}
