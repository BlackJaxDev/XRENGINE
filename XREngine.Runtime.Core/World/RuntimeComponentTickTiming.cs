namespace XREngine;

/// <summary>
/// Opt-in timing of world tick callbacks by owning component. While no recorder
/// is installed the tick dispatch reads this one static field per queue and does
/// nothing else; with a recorder installed it times each callback and reports it
/// with the component that owns it.
/// </summary>
public static class RuntimeComponentTickTiming
{
    private static IRuntimeComponentTickTimingRecorder? s_recorder;

    /// <summary>The recorder that receives callback timings, or null while timing is off.</summary>
    public static IRuntimeComponentTickTimingRecorder? Recorder
    {
        get => Volatile.Read(ref s_recorder);
        set => Volatile.Write(ref s_recorder, value);
    }
}
