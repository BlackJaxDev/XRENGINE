namespace XREngine.Rendering;

/// <summary>
/// Identifies the frame section that is open on the current thread for <see cref="CollectVisibleStageTimings"/>.
/// A thread records stage times only while one of its sections is open.
/// </summary>
internal enum ECollectVisibleSection
{
    /// <summary>No section is open. The stage timers record nothing.</summary>
    None = 0,

    /// <summary>The collect phase of the engine timer: the pre-collect, collect and post-collect events.</summary>
    Collect,

    /// <summary>The serial swap of the engine timer, outside the world swap.</summary>
    Swap,

    /// <summary>The world swap inside the serial swap. Shared swap code records only in this section.</summary>
    WorldSwap,
}
