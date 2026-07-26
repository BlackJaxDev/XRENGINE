namespace XREngine.Rendering.Compute;

/// <summary>
/// Describes whether renderer work was accepted into the ordered frame command stream.
/// </summary>
public enum PhysicsChainComputeEnqueueStatus
{
    Enqueued,
    ProgramPending,
    NoPassContext,
    DescriptorInvalid,
    InvalidResource,
    DeviceLost,
    Unsupported,
}
