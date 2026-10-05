using XREngine.Components;

namespace XREngine;

/// <summary>
/// Receives how long each world tick callback owned by a component took, while
/// per-component tick timing is on. The profiler implements it and installs
/// itself through <see cref="RuntimeComponentTickTiming.Recorder"/>.
/// </summary>
public interface IRuntimeComponentTickTimingRecorder
{
    /// <summary>
    /// Records one callback invocation. Called on the thread that dispatched the
    /// tick group, which may be the update or the fixed-update thread.
    /// </summary>
    void RecordComponentTick(XRComponent component, ETickGroup group, long elapsedStopwatchTicks);
}
