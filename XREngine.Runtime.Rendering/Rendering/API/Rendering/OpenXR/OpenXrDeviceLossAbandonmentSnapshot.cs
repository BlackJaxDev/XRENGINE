using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrDeviceLossAbandonmentSnapshot
{
    public long Epoch { get; set; }
    public OpenXrDeviceLossSource Source { get; set; }
    public OpenXrDeviceLossSettlementState SettlementState { get; set; }
    public bool ManagedTerminal { get; set; }
    public int AbandonedGenerationCount { get; set; }
    public int AbandonedSwapchainCount { get; set; }
    public int AbandonedAcquiredSwapchainCount { get; set; }
    public int ChildDestroyAttempted { get; set; }
    public int ChildDestroySucceeded { get; set; }
    public int ChildDestroyFailed { get; set; }
    public bool SessionDestroyAttempted { get; set; }
    public Result? SessionDestroyResult { get; set; }
    public string? SessionDestroyExceptionCategory { get; set; }
    public ulong QuarantinedSessionHandle { get; set; }
    public ulong QuarantinedAppSpaceHandle { get; set; }
    public bool InstanceDestroyAttempted { get; set; }
    public Result? InstanceDestroyResult { get; set; }
    public string? InstanceDestroyExceptionCategory { get; set; }
    public ulong QuarantinedInstanceHandle { get; set; }
    public string[] QuarantinedChildHandles { get; set; } = [];
    public string? QuarantineReason { get; set; }
    public OpenXrDeviceLossNativeParentDisposition NativeParentDisposition { get; set; }
}
