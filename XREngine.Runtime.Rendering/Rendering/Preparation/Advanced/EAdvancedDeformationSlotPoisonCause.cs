namespace XREngine.Rendering;

/// <summary>
/// Why <see cref="AdvancedGpuDeformationResources"/> marked a deformation output slot as never
/// reusable. A poisoned slot stays poisoned until the renderer is retired, so every later
/// preparation that lands on it defers.
/// </summary>
public enum EAdvancedDeformationSlotPoisonCause
{
    /// <summary>The backend accepted deformation work but returned no completion fence.</summary>
    NoProducerFence,
    /// <summary>The slot still held a producer fence when new work was recorded into it.</summary>
    ProducerFenceAlreadyPresent,
    /// <summary>The dispatch threw after work may have been accepted.</summary>
    DispatchException,
    /// <summary>Number of values.</summary>
    Count,
}
