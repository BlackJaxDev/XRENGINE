namespace XREngine.Rendering;

/// <summary>
/// Stable resource names for the RVC shadelet telemetry counters that the Advanced stage
/// family declares when <see cref="RvcRenderPipeline"/> hosts it.
/// </summary>
/// <remarks>
/// Slots below <see cref="AdvancedFrameSlotContract.DefaultSlotCount"/> return cached
/// strings, so frame-rate callers do not allocate.
/// </remarks>
public static class RvcShadeletTelemetryResourceNames
{
    public const string BaseCounters = "RvcShadeletTelemetry.Counters";

    private static readonly string[] CounterSlotNames = CreateSlotNames();

    /// <summary>Gets the telemetry counter buffer name for a frame slot.</summary>
    public static string Counters(uint slot)
        => slot < AdvancedFrameSlotContract.DefaultSlotCount
            ? CounterSlotNames[slot]
            : $"{BaseCounters}.Slot{slot}";

    private static string[] CreateSlotNames()
    {
        string[] names = new string[AdvancedFrameSlotContract.DefaultSlotCount];
        for (uint slot = 0u; slot < names.Length; slot++)
            names[slot] = $"{BaseCounters}.Slot{slot}";
        return names;
    }
}
