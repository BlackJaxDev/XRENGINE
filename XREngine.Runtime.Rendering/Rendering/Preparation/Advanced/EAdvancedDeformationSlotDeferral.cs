namespace XREngine.Rendering;

/// <summary>
/// Why <see cref="AdvancedGpuDeformationResources"/> could not open its deformation frame, so the
/// shared Advanced preparation deferred. Each value names the check that failed.
/// </summary>
public enum EAdvancedDeformationSlotDeferral
{
    /// <summary>The frame opened.</summary>
    None,
    /// <summary>The slot's output was accepted without a completion marker earlier, so its lifetime is unknown.</summary>
    SlotPoisoned,
    /// <summary>The command stream that holds the slot's producer fence has not been submitted yet.</summary>
    AwaitingSubmission,
    /// <summary>The producer fence was submitted and the GPU has not reached its timeline value.</summary>
    PendingCompletion,
    /// <summary>Polling the producer fence failed.</summary>
    FencePollFailed,
    /// <summary>The fence completed or failed, but the backend reports a command buffer that still uses the slot's output buffer.</summary>
    NativeConsumers,
    /// <summary>The backend cannot answer the output buffer reuse query.</summary>
    NativeQueryUnsupported,
    /// <summary>The device is lost.</summary>
    DeviceLost,
    /// <summary>The output slot was free, but no static input generation could be selected for the source set.</summary>
    StaticGeneration,
    /// <summary>The output slot was free, but replacing the output buffers for a larger capacity failed.</summary>
    OutputGrowth,
    /// <summary>Number of values.</summary>
    Count,
}
