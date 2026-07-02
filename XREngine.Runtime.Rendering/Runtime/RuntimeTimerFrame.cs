using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeTimerFrame(ERuntimeTimerFrameKind kind)
{
    [ThreadStatic]
    private static float? s_scopedRenderDeltaSeconds;

    public float Delta
    {
        get
        {
            if (kind == ERuntimeTimerFrameKind.Render && s_scopedRenderDeltaSeconds is { } scopedDeltaSeconds)
                return scopedDeltaSeconds;

            return (float)(kind == ERuntimeTimerFrameKind.Update
                ? RuntimeRenderingHostServices.Current.UpdateDeltaSeconds
                : RuntimeRenderingHostServices.Current.RenderDeltaSeconds);
        }
    }

    public long LastTimestampTicks
        => kind == ERuntimeTimerFrameKind.Update
            ? RuntimeRenderingHostServices.Current.LastUpdateTimestampTicks
            : RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;

    internal static ScopedRenderDeltaOverride PushScopedRenderDeltaSeconds(float deltaSeconds)
        => new(deltaSeconds);

    internal readonly struct ScopedRenderDeltaOverride : IDisposable
    {
        private readonly float? _previousDeltaSeconds;

        public ScopedRenderDeltaOverride(float deltaSeconds)
        {
            _previousDeltaSeconds = s_scopedRenderDeltaSeconds;
            s_scopedRenderDeltaSeconds = MathF.Max(0.0f, deltaSeconds);
        }

        public void Dispose()
            => s_scopedRenderDeltaSeconds = _previousDeltaSeconds;
    }
}
