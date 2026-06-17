using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeTimerFrame(ERuntimeTimerFrameKind kind)
{
    public float Delta
        => (float)(kind == ERuntimeTimerFrameKind.Update
            ? RuntimeRenderingHostServices.Current.UpdateDeltaSeconds
            : RuntimeRenderingHostServices.Current.RenderDeltaSeconds);

    public long LastTimestampTicks
        => kind == ERuntimeTimerFrameKind.Update
            ? RuntimeRenderingHostServices.Current.LastUpdateTimestampTicks
            : RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
}
