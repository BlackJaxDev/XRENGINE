using XREngine.Timers;

namespace XREngine;

/// <summary>
/// Publishes the application-owned engine timer through the lower runtime timing contract.
/// The renderer/window loop remains outside Runtime.Core.
/// </summary>
internal sealed class EngineRuntimeTimingServices(EngineTimer timer) : IRuntimeTimingServices
{
    public long ElapsedTicks => timer.TimeTicks();
    public long UpdateDeltaTicks => timer.Update.DeltaTicks;
    public long FixedDeltaTicks => timer.FixedUpdateDeltaTicks;
    public float UpdateDeltaSeconds => timer.Update.DilatedDelta;
    public float FixedDeltaSeconds => timer.FixedUpdateDelta;

    public event Action? Update
    {
        add { if (value is not null) timer.UpdateFrame += value; }
        remove { if (value is not null) timer.UpdateFrame -= value; }
    }
}
