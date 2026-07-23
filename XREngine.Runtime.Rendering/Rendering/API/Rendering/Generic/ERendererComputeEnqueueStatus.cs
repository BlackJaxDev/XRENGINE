namespace XREngine.Rendering;

/// <summary>Result of accepting a compute dispatch into a renderer command stream.</summary>
public enum ERendererComputeEnqueueStatus
{
    Enqueued,
    ProgramPending,
    NoPassContext,
    DescriptorInvalid,
    InvalidResource,
    DeviceLost,
    Unsupported,
}
