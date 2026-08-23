namespace XREngine.Data.Rendering;

/// <summary>
/// Selects the operating-system scheduling policy requested for persistent
/// renderer-neutral render-critical worker lanes.
/// </summary>
public enum ERenderWorkerQos : byte
{
    /// <summary>
    /// Leaves thread scheduling policy under the operating-system default.
    /// </summary>
    OsDefault = 0,

    /// <summary>
    /// Requests an explicitly measured high-priority policy. The execution
    /// scheduler must treat this as diagnostic until hardware validation passes.
    /// </summary>
    High = 1,
}
